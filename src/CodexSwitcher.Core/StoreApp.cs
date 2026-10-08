using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CodexSwitcher.Core;

public sealed record StorePackage(string InstallLocation,string FamilyName,string ApplicationId,string Executable,string Cli)
{
    public string Aumid => FamilyName + "!" + ApplicationId;
    public static async Task<StorePackage> FindAsync(CancellationToken ct=default)
    {
        var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell/v1.0/powershell.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[] {"-NoProfile","-NonInteractive","-Command","[Console]::OutputEncoding = [Text.UTF8Encoding]::new(); Get-AppxPackage -Name OpenAI.Codex | Select-Object InstallLocation,PackageFamilyName | ConvertTo-Json -Compress"}) info.ArgumentList.Add(arg);
        using var process=Process.Start(info) ?? throw new UserFault("Не удалось найти Codex из Microsoft Store.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var output=process.StandardOutput.ReadToEndAsync(deadline.Token); var error=process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if(!process.HasExited) process.Kill(true); throw new UserFault("Поиск приложения Codex не завершился. Повторите операцию."); }
        _=await error;
        var json=await output;
        if(process.ExitCode!=0 || string.IsNullOrWhiteSpace(json)) throw new UserFault("Codex из Microsoft Store не найден. Установите или откройте приложение.");
        using var doc=JsonDocument.Parse(json);
        var root=doc.RootElement.ValueKind==JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
        var location=root.GetProperty("InstallLocation").GetString()!; var family=root.GetProperty("PackageFamilyName").GetString()!;
        using var reader=XmlReader.Create(Path.Combine(location,"AppxManifest.xml"),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit});
        var manifest=XDocument.Load(reader);
        var app=manifest.Descendants().FirstOrDefault(x=>x.Name.LocalName=="Application" && (string?)x.Attribute("Id")=="App")
            ?? throw new UserFault("Неподдерживаемый пакет Codex.");
        var relative=(string?)app.Attribute("Executable") ?? throw new UserFault("Путь запуска Codex не найден.");
        var exe=Path.GetFullPath(Path.Combine(location,relative.Replace('/',Path.DirectorySeparatorChar)));
        var cli=Path.Combine(location,"app","resources","codex.exe");
        if(!exe.StartsWith(location+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || !File.Exists(exe) || !File.Exists(cli))
            throw new UserFault("Пакет Codex изменился. Обновите переключатель.");
        return new StorePackage(location,family,"App",exe,cli);
    }
}

public sealed class StoreAppController(StorePackage package) : IAppController
{
    private readonly Dictionary<int,long> descendants=new();
    private IEnumerable<Process> AppProcesses()
    {
        foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(package.Executable)))
        {
            bool ours;
            try {ours=string.Equals(process.MainModule?.FileName,package.Executable,StringComparison.OrdinalIgnoreCase);}
            catch {process.Dispose();throw new UserFault("Не удалось проверить процесс приложения. Закройте Codex вручную.");}
            if(ours) yield return process; else process.Dispose();
        }
    }
    public async Task CloseAsync(CancellationToken ct)
    {
        var processes=AppProcesses().ToList();
        try
        {
            CaptureDescendants(processes.Select(p=>p.Id).ToHashSet());
            // Electron renderers share the executable name. Register only the main
            // app process (whose parent is outside this set), never its CLI/tools.
            var ids=processes.Select(p=>p.Id).ToHashSet();
            var parents=ReadParents();
            var roots=processes.Where(p=>parents.TryGetValue(p.Id,out var parent) && !ids.Contains(parent)).ToList();
            if(processes.Count>0 && roots.Count==0) throw new UserFault("Не удалось определить главный процесс Codex. Выберите «Выход» в его меню.");
            await GracefulAppExit.RequestAsync(roots,ct);
            var end=DateTime.UtcNow.AddSeconds(30);
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                try {EnsureStopped();return;} catch(UserFault) { if(DateTime.UtcNow>=end) throw new UserFault("Codex не завершился за 30 секунд. Завершите задачи и выберите «Выход» в меню или трее Codex."); }
                await Task.Delay(250,ct);
            }
        }
        finally {foreach(var process in processes) process.Dispose();}
    }
    public void EnsureStopped()
    {
        var running=AppProcesses().ToList();
        try {if(running.Any()) throw new UserFault("Codex ещё работает. Дождитесь закрытия приложения.");}
        finally {foreach(var p in running)p.Dispose();}
        foreach(var entry in descendants)
        {
            try {using var p=Process.GetProcessById(entry.Key); if(!p.HasExited && p.StartTime.ToUniversalTime().Ticks==entry.Value)
                throw new UserFault("Дочерний процесс Codex ещё работает. Дождитесь завершения задач.");}
            catch(ArgumentException) { }
        }
        foreach(var process in Process.GetProcessesByName("codex"))
        {
            using(process) if(!process.HasExited) throw new UserFault("Работает другая сессия Codex CLI или расширение. Закройте её перед переключением.");
        }
    }
    public async Task LaunchAsync(CancellationToken ct)
    {
        using var shell=Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"))
        {Arguments="shell:AppsFolder\\"+package.Aumid,UseShellExecute=true});
        var end=DateTime.UtcNow.AddSeconds(20);
        while(DateTime.UtcNow<end)
        {
            ct.ThrowIfCancellationRequested(); var running=AppProcesses().ToList();
            try {if(running.Any())return;} finally {foreach(var p in running)p.Dispose();}
            await Task.Delay(300,ct);
        }
        throw new UserFault("Аккаунт переключён, но Codex не открылся. Откройте его через меню «Пуск».");
    }
    private void CaptureDescendants(HashSet<int> parents)
    {
        descendants.Clear(); var snapshot=CreateToolhelp32Snapshot(2,0);
        if(snapshot==new IntPtr(-1)) throw new UserFault("Не удалось проверить дочерние процессы Codex.");
        try
        {
            var rows=new List<(int Id,int Parent)>(); var row=new ProcessEntry {Size=(uint)Marshal.SizeOf<ProcessEntry>()};
            if(Process32First(snapshot,ref row)) do {rows.Add(((int)row.Id,(int)row.Parent));} while(Process32Next(snapshot,ref row));
            bool added;
            do {added=false;foreach(var item in rows) if(parents.Contains(item.Parent) && parents.Add(item.Id)) added=true;} while(added);
            foreach(var id in parents)
                try {using var p=Process.GetProcessById(id); descendants[id]=p.StartTime.ToUniversalTime().Ticks;} catch(ArgumentException) { }
        }
        finally {CloseHandle(snapshot);}
    }
    private static Dictionary<int,int> ReadParents()
    {
        var snapshot=CreateToolhelp32Snapshot(2,0);
        if(snapshot==new IntPtr(-1)) throw new UserFault("Не удалось проверить процессы Codex.");
        try
        {
            var result=new Dictionary<int,int>();var row=new ProcessEntry {Size=(uint)Marshal.SizeOf<ProcessEntry>()};
            if(Process32First(snapshot,ref row)) do {result[(int)row.Id]=(int)row.Parent;} while(Process32Next(snapshot,ref row));
            return result;
        }
        finally {CloseHandle(snapshot);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size,Usage,Id; public UIntPtr Heap; public uint Module,Threads,Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Exe;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="Process32FirstW")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="Process32NextW")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
