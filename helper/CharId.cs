using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// ============ 角色识别（注册 + 匹配：CLIP 裁剪嵌入 + 自动学习 + 未知聚类治理） ============
static class CharId
{
    static readonly object L = new object();
    static Dictionary<string, List<float[]>> _reg;
    static DateTime _regMtime = DateTime.MinValue;
    static int _autoLearnedCount = 0;

    static string RegFile() => Path.Combine(PaddleOcr.ModelDir(), "characters.json");

    static Dictionary<string, List<float[]>> Load()
    {
        lock (L)
        {
            string f = RegFile();
            DateTime mtime = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
            if (_reg != null && mtime == _regMtime) return _reg;
            _reg = new Dictionary<string, List<float[]>>();
            try
            {
                if (File.Exists(f))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        var arr = p.Value.EnumerateArray().ToList();
                        if (arr.Count == 0) continue;
                        if (arr[0].ValueKind == JsonValueKind.Number)
                            _reg[p.Name] = new List<float[]> { arr.Select(v => (float)v.GetDouble()).ToArray() };
                        else
                            _reg[p.Name] = arr.Select(a => a.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray()).ToList();
                    }
                    _regMtime = mtime;
                }
            }
            catch { }
            return _reg;
        }
    }

    public static int Register(string name, float[] emb)
    {
        var reg = Load();
        lock (L)
        {
            if (!reg.TryGetValue(name, out var list)) { list = new List<float[]>(); reg[name] = list; }
            list.Add(emb);
            Save(reg);
            return list.Count;
        }
    }

    public static bool Merge(string from, string to)
    {
        var reg = Load();
        lock (L)
        {
            if (!reg.TryGetValue(from, out var src)) return false;
            if (!reg.TryGetValue(to, out var dst)) { dst = new List<float[]>(); reg[to] = dst; }
            dst.AddRange(src);
            reg.Remove(from);
            Save(reg);
            return true;
        }
    }

    static double Dot(float[] a, float[] b)
    {
        double dot = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) dot += a[i] * b[i];
        return dot;
    }

    public static (string name, double score) Match(float[] emb, double minScore = 0.70)
    {
        var reg = Load();
        string best = null; double bestS = -1;
        lock (L)
        {
            foreach (var kv in reg)
            {
                foreach (var e in kv.Value)
                {
                    double dot = Dot(emb, e);
                    if (dot > bestS) { bestS = dot; best = kv.Key; }
                }
            }
        }
        if (best == null) return (null, bestS);
        return (bestS >= minScore ? best : null, bestS);
    }

    public static List<string> Names() { lock (L) return Load().Keys.ToList(); }
    public static int AutoLearnedCount { get { lock (L) return _autoLearnedCount; } }

    public sealed class UnknownEntry
    {
        public List<float[]> Refs = new List<float[]>();
        public int Count;
        public string FirstSeen = "", LastSeen = "";
        public double BestNamedScore;
    }
    static Dictionary<string, UnknownEntry> _unknowns;
    static DateTime _unkMtime = DateTime.MinValue;
    static long _lastUnkSaveTick = 0;
    static int _unknownSeq = 1;
    const int MaxUnknownClusters = 50;
    const int MaxNamedRefs = 8;

    static string UnknownFile() => Path.Combine(PaddleOcr.ModelDir(), "characters-unknown.json");

    static Dictionary<string, UnknownEntry> LoadUnknowns()
    {
        lock (L)
        {
            string f = UnknownFile();
            DateTime mtime = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
            if (_unknowns != null && mtime == _unkMtime) return _unknowns;
            _unknowns = new Dictionary<string, UnknownEntry>();
            try
            {
                if (File.Exists(f))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        var e = new UnknownEntry();
                        if (p.Value.TryGetProperty("refs", out var refs))
                            e.Refs = refs.EnumerateArray().Select(a => a.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray()).ToList();
                        if (p.Value.TryGetProperty("count", out var cnt)) e.Count = cnt.GetInt32();
                        if (p.Value.TryGetProperty("firstSeen", out var fs)) e.FirstSeen = fs.GetString();
                        if (p.Value.TryGetProperty("lastSeen", out var ls)) e.LastSeen = ls.GetString();
                        if (p.Value.TryGetProperty("bestNamedScore", out var bs)) e.BestNamedScore = bs.GetDouble();
                        _unknowns[p.Name] = e;
                        int n = 0;
                        var num = p.Name.Split('#').LastOrDefault();
                        if (int.TryParse(num, out n) && n >= _unknownSeq) _unknownSeq = n + 1;
                    }
                    _unkMtime = mtime;
                }
            }
            catch { }
            return _unknowns;
        }
    }

    static void SaveUnknowns()
    {
        string f = UnknownFile();
        var obj = (_unknowns ?? new Dictionary<string, UnknownEntry>()).ToDictionary(kv => kv.Key, kv => new Dictionary<string, object>
        {
            { "refs", kv.Value.Refs.Select(e => e.Select(v => (double)v).ToArray()).ToArray() },
            { "count", kv.Value.Count },
            { "firstSeen", kv.Value.FirstSeen },
            { "lastSeen", kv.Value.LastSeen },
            { "bestNamedScore", kv.Value.BestNamedScore },
        });
        WriteAtomic(f, JsonSerializer.Serialize(obj));
        _unkMtime = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
        _lastUnkSaveTick = Environment.TickCount64;
    }

    static (string name, double score) MatchNamed(float[] emb, double minScore = 0.70)
    {
        var (nm, sc) = Match(emb, double.MinValue);   // 取全表最优
        return (sc >= minScore ? nm : null, sc);
    }

    // 已知优先（高置信且与锚点一致时自动学习补充多样参考，上限 8 防漂移）；
    // 低于阈值则归入未知角色聚类（相似≥0.85 复用，新参考需多样性<0.96，落盘节流 5s，聚类上限 50）
    public static (string name, double score, string unknownId) MatchOrUnknown(float[] emb, double namedMin = 0.70, double unknownMin = 0.85)
    {
        var (named, namedScore) = MatchNamed(emb, namedMin);
        if (named != null)
        {
            // C4: 画廊自动学习（高置信 >=0.86、非重复帧 <0.965、与首张锚点参考 >=0.78 防漂移、总数 <8）
            if (namedScore >= 0.86 && namedScore < 0.965)
            {
                var reg = Load();
                lock (L)
                {
                    if (reg.TryGetValue(named, out var refs) && refs.Count > 0 && refs.Count < MaxNamedRefs)
                    {
                        double anchorSim = Dot(emb, refs[0]);
                        if (anchorSim >= 0.78)
                        {
                            refs.Add(emb);
                            _autoLearnedCount++;
                            Save(reg);
                        }
                    }
                }
            }
            return (named, namedScore, null);
        }
        var unk = LoadUnknowns();
        lock (L)
        {
            string bestId = null; double bestSim = -1;
            foreach (var kv in unk)
                foreach (var r in kv.Value.Refs)
                {
                    double dot = Dot(emb, r);
                    if (dot > bestSim) { bestSim = dot; bestId = kv.Key; }
                }
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (bestId != null && bestSim >= unknownMin)
            {
                var e = unk[bestId];
                e.Count++; e.LastSeen = now; e.BestNamedScore = Math.Max(e.BestNamedScore, namedScore);
                bool addedRef = false;
                // 多样性过滤：与已有参考相似度 <0.96 才收入新参考，避免连续静帧占满 5 个槽位
                if (e.Refs.Count < 5 && bestSim < 0.96)
                {
                    e.Refs.Add(emb);
                    addedRef = true;
                }
                if (addedRef || Environment.TickCount64 - _lastUnkSaveTick >= 5000)
                {
                    SaveUnknowns();
                }
                return (null, bestSim, bestId);
            }
            // C6: 聚类容量上限治理（超出 50 个时优先淘汰最早的 Count<=1 噪声聚类）
            if (unk.Count >= MaxUnknownClusters)
            {
                var victim = unk
                    .OrderBy(kv => kv.Value.Count <= 1 ? 0 : 1)
                    .ThenBy(kv => kv.Value.LastSeen)
                    .FirstOrDefault();
                if (victim.Key != null) unk.Remove(victim.Key);
            }
            string id = "未知角色#" + (_unknownSeq++);
            unk[id] = new UnknownEntry { Refs = new List<float[]> { emb }, Count = 1, FirstSeen = now, LastSeen = now, BestNamedScore = namedScore };
            SaveUnknowns();
            return (null, namedScore, id);
        }
    }

    public static bool Promote(string unknownId, string name)
    {
        var unk = LoadUnknowns();
        lock (L)
        {
            if (!unk.TryGetValue(unknownId, out var e)) return false;
            var reg = Load();
            if (!reg.TryGetValue(name, out var list)) { list = new List<float[]>(); reg[name] = list; }
            list.AddRange(e.Refs);
            Save(reg);
            unk.Remove(unknownId);
            SaveUnknowns();
            return true;
        }
    }

    public static bool Ignore(string unknownId)
    {
        var unk = LoadUnknowns();
        lock (L)
        {
            if (unknownId == "*" || unknownId == "all")
            {
                int n = unk.Count;
                unk.Clear();
                SaveUnknowns();
                return n >= 0;
            }
            if (unknownId == "stale")
            {
                var staleKeys = unk.Where(kv => kv.Value.Count <= 1).Select(kv => kv.Key).ToList();
                foreach (var k in staleKeys) unk.Remove(k);
                if (staleKeys.Count > 0) SaveUnknowns();
                return true;
            }
            if (!unk.Remove(unknownId)) return false;
            SaveUnknowns();
            return true;
        }
    }

    public static int CleanupUnknowns(int maxCount = 1)
    {
        var unk = LoadUnknowns();
        lock (L)
        {
            var keys = unk.Where(kv => kv.Value.Count <= maxCount).Select(kv => kv.Key).ToList();
            foreach (var k in keys) unk.Remove(k);
            if (keys.Count > 0) SaveUnknowns();
            return keys.Count;
        }
    }

    public static List<Dictionary<string, object>> ListUnknowns()
    {
        var unk = LoadUnknowns();
        lock (L)
            return unk.Select(kv => new Dictionary<string, object> {
                { "id", kv.Key }, { "refs", kv.Value.Refs.Count }, { "count", kv.Value.Count },
                { "firstSeen", kv.Value.FirstSeen }, { "lastSeen", kv.Value.LastSeen },
                { "bestNamedScore", Math.Round(kv.Value.BestNamedScore, 3) },
            }).ToList();
    }

    static Dictionary<string, string> _notes;
    static DateTime _notesMtime = DateTime.MinValue;
    static string NotesFile() => Path.Combine(PaddleOcr.ModelDir(), "characters-notes.json");

    static void EnsureNotesLoaded()
    {
        string f = NotesFile();
        DateTime mtime = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
        if (_notes != null && mtime == _notesMtime) return;
        _notes = new Dictionary<string, string>();
        try
        {
            if (File.Exists(f))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                foreach (var p in doc.RootElement.EnumerateObject())
                    _notes[p.Name] = p.Value.GetString();
                _notesMtime = mtime;
            }
        }
        catch { }
    }

    static void SaveNotes()
    {
        string f = NotesFile();
        var obj = (_notes ?? new Dictionary<string, string>()).ToDictionary(kv => kv.Key, kv => kv.Value);
        WriteAtomic(f, JsonSerializer.Serialize(obj));
        _notesMtime = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
    }

    public static string NoteOf(string name)
    {
        lock (L)
        {
            EnsureNotesLoaded();
            return _notes.TryGetValue(name, out var v) ? v : null;
        }
    }

    public static bool Delete(string name)
    {
        var reg = Load();
        lock (L)
        {
            if (!reg.Remove(name)) return false;
            Save(reg);
            EnsureNotesLoaded();
            if (_notes.Remove(name)) SaveNotes();
            return true;
        }
    }

    public static bool Rename(string oldName, string newName)
    {
        var reg = Load();
        lock (L)
        {
            if (!reg.TryGetValue(oldName, out var emb)) return false;
            reg.Remove(oldName);
            reg[newName] = emb;
            Save(reg);
            EnsureNotesLoaded();
            if (_notes.TryGetValue(oldName, out var note))
            {
                _notes.Remove(oldName);
                _notes[newName] = note;
                SaveNotes();
            }
            return true;
        }
    }

    static void WriteAtomic(string path, string json)
    {
        string tmp = path + "." + Environment.ProcessId + "." + System.Threading.Thread.CurrentThread.ManagedThreadId + ".tmp";
        File.WriteAllText(tmp, json);
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch
        {
            try { File.Copy(tmp, path, true); File.Delete(tmp); } catch { }
        }
    }

    static void Save(Dictionary<string, List<float[]>> reg)
    {
        string f = RegFile();
        var obj = reg.ToDictionary(kv => kv.Key, kv => kv.Value.Select(e => e.Select(v => (double)v).ToArray()).ToArray());
        WriteAtomic(f, JsonSerializer.Serialize(obj));
        _regMtime = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
    }

    public static bool SetNote(string name, string note)
    {
        lock (L)
        {
            EnsureNotesLoaded();
            if (!Load().ContainsKey(name)) return false;
            _notes[name] = note;
            SaveNotes();
            return true;
        }
    }

    public static List<Dictionary<string, object>> List()
    {
        var reg = Load();
        lock (L)
            return reg.Select(kv => new Dictionary<string, object> {
                { "name", kv.Key },
                { "dim", kv.Value[0].Length },
                { "refs", kv.Value.Count },
                { "note", NoteOf(kv.Key) }
            }).ToList();
    }
}
