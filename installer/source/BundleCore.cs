using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal sealed class BundlePayload
{
    internal const string ModId = "zune-hd-zh-hans";
    internal const string Root = @"\flash2\automation\mods\zune-hd-zh-hans";
    internal const string ManifestPath = Root + @"\manifest.json";
    internal const string MarkerPath = @"\flash2\automation\lyra.json";
    internal const string BootPath = @"\flash2\automation\boot.state";
    internal const string EnabledPath = @"\flash2\automation\mods\enabled.json";
    internal const string BootLogPath = @"\flash2\automation\mods\boot.log";
    internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    internal readonly SortedDictionary<string, byte[]> Blobs = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
    internal readonly Dictionary<string, string> Baselines = new Dictionary<string, string>(StringComparer.Ordinal);
    internal readonly byte[] Manifest;
    internal readonly string ManifestHash;
    internal readonly Dictionary<string, string> Components = new Dictionary<string, string> {
        {"clock-date", "eff9302dd7d93905785032bf59c4d19d4f6d631a6251b4581506ec88453c98d2"},
        {"mods-tab", "44ba0b6c271f5573d24750d616716292cf37b2595fa9df2bd6e45bea0b8b5442"},
        {"wifi-power", "266f761b56b84121a621fbbd4a7d701c32766578866b99e7b8659ed6e94f47c5"}
    };
    internal static byte[] Resource(string name)
    {
        using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var output = new MemoryStream())
        { if (input == null) throw new IOException("内置文件缺失。"); input.CopyTo(output); return output.ToArray(); }
    }
    internal static string Hash(byte[] data)
    {
        if (data == null) return null;
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }
    internal static void Require(byte[] data, string expected)
    { if (Hash(data) != expected) throw new IOException("文件校验不一致，操作已停止。"); }
    internal static Dictionary<string, object> Parse(byte[] data)
    {
        if (data == null) throw new IOException("必要记录缺失。");
        var value = Json.DeserializeObject(new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF')) as Dictionary<string, object>;
        if (value == null) throw new IOException("记录格式无效。");
        return value;
    }
    internal BundlePayload()
    {
        byte[] zip = Resource("bundle.zip"), specBytes = Resource("bundle.spec");
        Require(zip, BundleConstants.ArchiveHash); Require(specBytes, BundleConstants.SpecHash);
        var spec = Parse(specBytes);
        var hashes = (Dictionary<string, object>)spec["file_sha256"];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string path = entry.FullName;
                if (!seen.Add(path) || !hashes.ContainsKey(path) || entry.Length <= 0 || entry.Length > 131072) throw new IOException("内置包的文件集合异常。");
                if (path != "manifest.json" && !Regex.IsMatch(path, @"\Apayload-0\.2\.[23]/tables/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+\.xus\z")) throw new IOException("内置包的路径异常。");
                foreach (string part in path.Split('/'))
                    if (part.EndsWith(".") || part == "." || part == ".." || Regex.IsMatch(part, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|\z)", RegexOptions.IgnoreCase)) throw new IOException("保留路径不能用于此包。");
                if ((Root + "\\" + path).Length >= 220) throw new IOException("内置包的路径过长。");
                byte[] bytes;
                using (var stream = entry.Open()) using (var output = new MemoryStream()) { stream.CopyTo(output); bytes = output.ToArray(); }
                Require(bytes, (string)hashes[path]);
                if (path == "manifest.json") Manifest = bytes; else Blobs.Add(path, bytes);
            }
        }
        if (seen.Count != hashes.Count || Blobs.Count != 103 || Manifest == null || Manifest.Length >= 65536) throw new IOException("内置包不完整。");
        ManifestHash = (string)spec["manifest_sha256"]; Require(Manifest, ManifestHash);
        var manifest = Parse(Manifest);
        if ((string)manifest["mod_id"] != ModId || (string)manifest["version"] != "0.2.3") throw new IOException("内置版本不符。");
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (object row in (object[])manifest["actions"])
        {
            var action = (Dictionary<string, object>)row;
            string reference = (string)action["new_content_ref"];
            if ((string)action["type"] != "lyra.gem_replace_entry" || !((string)action["entry_name"]).StartsWith(@"ESP\") || !reference.StartsWith("@/")) throw new IOException("内置操作超出语言表范围。");
            string path = reference.Substring(2);
            if (!Blobs.ContainsKey(path) || !references.Add(path)) throw new IOException("内置表引用异常。");
        }
        if (references.Count != Blobs.Count) throw new IOException("内置表引用不完整。");
        foreach (object row in (object[])spec["resources"])
        { var item = (Dictionary<string, object>)row; Baselines.Add((string)item["gem"], (string)item["baseline_sha256"]); }
        if (Baselines.Count != 10 || Baselines.Keys.Any(n => !Regex.IsMatch(n, @"\A[A-Za-z0-9_]+\.gem\z"))) throw new IOException("基线范围异常。");
    }
}

internal sealed class BundleState
{
    internal byte[] Marker, Boot, BootLog, Enabled, Manifest;
    internal bool EnabledChoice;
}

internal sealed class InstallJournal : IDisposable
{
    internal readonly string Folder;
    private readonly FileStream stream;
    internal InstallJournal(string root, string action)
    {
        Folder = Path.Combine(Path.GetFullPath(root), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0,8));
        Directory.CreateDirectory(Folder);
        stream = new FileStream(Path.Combine(Folder, "journal.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        Event("begin", action, null);
    }
    internal void Event(string operation, string path, string hash)
    {
        byte[] line = new UTF8Encoding(false).GetBytes(BundlePayload.Json.Serialize(new { utc = DateTime.UtcNow.ToString("o"), operation = operation, path = path, sha256 = hash }) + "\r\n");
        stream.Write(line, 0, line.Length); stream.Flush(true);
    }
    internal void Save(string name, byte[] data)
    {
        if (data == null) { Event("absent", name, null); return; }
        string target = Path.Combine(Folder, name);
        using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(data, 0, data.Length); file.Flush(true); }
        BundlePayload.Require(File.ReadAllBytes(target), BundlePayload.Hash(data));
        Event("saved", name, BundlePayload.Hash(data));
    }
    public void Dispose() { stream.Dispose(); }
}

internal sealed class BundleInstaller
{
    private readonly IModFiles files;
    private readonly BundlePayload payload;
    private readonly string backupRoot;
    private readonly Action<string> log;
    internal BundleInstaller(IModFiles target, string backup, Action<string> output)
    { files = target; payload = new BundlePayload(); backupRoot = backup; log = output; }
    private static bool IsEnabled(byte[] bytes)
    {
        if (bytes == null) return false;
        var data = BundlePayload.Parse(bytes);
        if ((int)data["version"] != 1 || !(data["enabled"] is object[])) throw new IOException("启用记录无效。");
        var ids = (object[])data["enabled"];
        if (ids.Length > 1 || ids.Any(id => !(id is string) || (string)id != BundlePayload.ModId)) throw new IOException("有其他功能插件启用，超出已核验范围。");
        return ids.Length == 1;
    }
    private BundleState ReadState(bool installGate)
    {
        var state = new BundleState();
        state.Marker = files.Read(BundlePayload.MarkerPath);
        var platform = BundlePayload.Parse(state.Marker);
        if ((string)platform["mod_id"] != "lyra" || (string)platform["version"] != "1.4.0") throw new IOException("此候选仅适用 Lyra 1.4.0。");
        state.Manifest = files.Read(BundlePayload.ManifestPath);
        if (state.Manifest != null && BundlePayload.Hash(state.Manifest) != payload.ManifestHash) throw new IOException("已有同名插件的其他清单，请先在英文中撤销旧版。");
        if (!installGate) return state;
        state.Boot = files.Read(BundlePayload.BootPath);
        var boot = BundlePayload.Parse(state.Boot);
        if ((int)boot["version"] != 1 || (string)boot["level"] != "normal" || (int)boot["failures"] != 0 || (int)boot["shell_started"] != 1) throw new IOException("本次启动尚未正常完成，安装已停止。");
        state.Enabled = files.Read(BundlePayload.EnabledPath); state.EnabledChoice = IsEnabled(state.Enabled);
        state.BootLog = files.Read(BundlePayload.BootLogPath);
        if (state.BootLog == null) throw new IOException("启动日志缺失，安装已停止。");
        if (Regex.IsMatch(Encoding.UTF8.GetString(state.BootLog), @"(?im)^.*(?:some flushes failed|flush:.*(?:short write|cannot create|failed|mismatch)).*$")) throw new IOException("上次启动的资源写入不完整，安装已停止。");
        foreach (var item in payload.Components)
            BundlePayload.Require(files.Read(@"\flash2\automation\platform\" + item.Key + @"\manifest.json"), item.Value);
        return state;
    }
    private void CheckUnchanged(BundleState expected)
    {
        BundleState now = ReadState(true);
        if (BundlePayload.Hash(now.Marker) != BundlePayload.Hash(expected.Marker) || BundlePayload.Hash(now.Boot) != BundlePayload.Hash(expected.Boot) || BundlePayload.Hash(now.BootLog) != BundlePayload.Hash(expected.BootLog) || BundlePayload.Hash(now.Enabled) != BundlePayload.Hash(expected.Enabled) || BundlePayload.Hash(now.Manifest) != BundlePayload.Hash(expected.Manifest)) throw new IOException("传输期间启动或启用状态有变化，清单尚未提交。");
    }
    private static string DevicePath(string path) { return BundlePayload.Root + "\\" + path.Replace('/', '\\'); }
    private void RecordUnconfirmed(InstallJournal journal, string operation, string path)
    {
        try { journal.Event(operation, path, null); }
        catch (Exception error) { log("结果日志未能补写：" + error.Message); }
    }
    private void VerifyPayload()
    { foreach (var item in payload.Blobs) BundlePayload.Require(files.Read(DevicePath(item.Key)), BundlePayload.Hash(item.Value)); }
    internal string Check()
    {
        BundleState state = ReadState(false);
        if (state.Manifest != null)
        {
            try { VerifyPayload(); }
            catch (Exception error) { return "0.2.3 清单存在，部分文件未通过核对，可先切回 English 再撤销。" + error.Message; }
            return "0.2.3 清单与 103 张表已核对。撤销前须先回 English。";
        }
        try { ReadState(true); }
        catch (Exception error) { return "已连接，但安装条件未满足：" + error.Message; }
        return "已连接，当前没有完整版清单。安装前会再次核对十个原版资源档。";
    }
    internal string Install()
    {
        BundleState before = ReadState(true);
        if (before.Manifest != null) { VerifyPayload(); return "0.2.3 已完整安装，不重复写入。"; }
        using (var journal = new InstallJournal(backupRoot, "install-0.2.3"))
        {
            log("记录与备份：" + journal.Folder);
            journal.Save("lyra.json", before.Marker); journal.Save("boot.state", before.Boot); journal.Save("enabled.json", before.Enabled); journal.Save("previous-manifest.json", before.Manifest);
            journal.Save("boot.log", before.BootLog);
            foreach (var item in payload.Baselines)
            {
                byte[] bytes = files.Read(@"\Windows\" + item.Key);
                BundlePayload.Require(bytes, item.Value); journal.Save("baseline-" + item.Key, bytes);
                if (files.Read(@"\Windows\" + item.Key + ".new") != null) throw new IOException("有未完成的临时资源，安装已停止：" + item.Key);
                log("原版资源已核对：" + item.Key);
            }
            CheckUnchanged(before);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BundlePayload.Root };
            foreach (string path in payload.Blobs.Keys)
            {
                string current = BundlePayload.Root;
                string[] parts = path.Split('/');
                for (int i = 0; i < parts.Length - 1; ++i) { current += "\\" + parts[i]; directories.Add(current); }
            }
            foreach (string directory in directories.OrderBy(s => s.Length).ThenBy(s => s, StringComparer.Ordinal))
            { journal.Event("mkdir-request", directory, null); files.MakeDirectory(directory); journal.Event("mkdir-ok", directory, null); }
            int count = 0;
            foreach (var item in payload.Blobs)
            {
                string destination = DevicePath(item.Key), hash = BundlePayload.Hash(item.Value);
                byte[] existing = files.Read(destination);
                if (existing != null)
                {
                    if (BundlePayload.Hash(existing) != hash) { journal.Save("unexpected-file-" + count + ".bin", existing); throw new IOException("版本目录内有不同内容的文件，已保留并停止：" + item.Key); }
                    journal.Event("verified-existing", destination, hash);
                }
                else
                {
                    string temporary = destination + ".part-" + Guid.NewGuid().ToString("N").Substring(0,8);
                    journal.Event("write-request", temporary, hash); files.Write(temporary, item.Value);
                    BundlePayload.Require(files.Read(temporary), hash); journal.Event("write-verified", temporary, hash);
                    if (files.Read(destination) != null) throw new IOException("传输中出现新文件，已停止提交。");
                    journal.Event("publish-file-request", destination, hash); files.Move(temporary, destination);
                    BundlePayload.Require(files.Read(destination), hash); journal.Event("published-file", destination, hash);
                }
                if (++count % 16 == 0 || count == payload.Blobs.Count) log("字符串表已校验 " + count + "/" + payload.Blobs.Count);
            }
            string pending = BundlePayload.Root + "\\manifest.pending-" + Guid.NewGuid().ToString("N");
            journal.Event("write-manifest-request", pending, payload.ManifestHash); files.Write(pending, payload.Manifest);
            BundlePayload.Require(files.Read(pending), payload.ManifestHash); journal.Event("manifest-verified", pending, payload.ManifestHash);
            CheckUnchanged(before);
            journal.Event("commit-request", pending, payload.ManifestHash);
            try { files.Move(pending, BundlePayload.ManifestPath); }
            catch (Exception error)
            {
                RecordUnconfirmed(journal, "commit-reply-unconfirmed", pending);
                throw new IOException("清单提交回复未确认；可能已提交。请重新检查连接，不要直接重启。" + error.Message, error);
            }
            // Publication succeeded. A later read/log failure never claims that nothing changed.
            try
            {
                BundlePayload.Require(files.Read(BundlePayload.ManifestPath), payload.ManifestHash);
                if (files.Read(pending) != null) throw new IOException("提交后的临时清单仍存在。");
                journal.Event("commit-verified", BundlePayload.ManifestPath, payload.ManifestHash);
            }
            catch (Exception error) { throw new IOException("清单已提交，后续核验未完成，请重新检查连接。" + error.Message, error); }
            return before.EnabledChoice ? "0.2.3 已传入并校验；沿用启用记录。先保持 English 重启并核验，再选 Español。" : "0.2.3 已传入并校验。请在 English 中到 Mods/manage 启用 Zune HD Chinese，先验证英文重启。";
        }
    }
    internal string Undo()
    {
        BundleState before = ReadState(false);
        if (before.Manifest == null) return "当前没有完整版安装清单。请保持 English，按顶部电源约 10 秒重启。";
        using (var journal = new InstallJournal(backupRoot, "undo-0.2.3"))
        {
            log("记录与备份：" + journal.Folder);
            journal.Save("lyra.json", before.Marker); journal.Save("previous-manifest.json", before.Manifest);
            foreach (string path in new[] { BundlePayload.BootPath, BundlePayload.EnabledPath })
            {
                try { journal.Save(path == BundlePayload.BootPath ? "boot.state" : "enabled.json", files.Read(path)); }
                catch (IOException error) { log("辅助记录未能保存：" + error.Message); }
            }
            BundlePayload.Require(files.Read(BundlePayload.ManifestPath), payload.ManifestHash);
            string removed = BundlePayload.ManifestPath + ".removed-0.2.3-" + Guid.NewGuid().ToString("N");
            journal.Event("withdraw-request", removed, payload.ManifestHash);
            try { files.Move(BundlePayload.ManifestPath, removed); }
            catch (Exception error) { RecordUnconfirmed(journal, "withdraw-reply-unconfirmed", removed); throw new IOException("撤销回复未确认，可能已撤下。请重新检查清单状态后再重启。" + error.Message, error); }
            try
            {
                BundlePayload.Require(files.Read(removed), payload.ManifestHash);
                if (files.Read(BundlePayload.ManifestPath) != null) throw new IOException("仍存在新的启用清单。");
                journal.Event("withdraw-verified", removed, payload.ManifestHash);
            }
            catch (Exception error) { throw new IOException("清单已移动，撤销后的核验未完成。请重新检查。" + error.Message, error); }
            return "0.2.3 清单已撤下，数据与备份保留。请保持 English，按顶部电源约 10 秒重启。";
        }
    }
}
