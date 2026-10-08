using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexSwitcher.Core;

// Restart Manager asks the registered application to save state and terminate,
// including when all its windows are hidden in the tray. No RmForceShutdown flag.
public static class GracefulAppExit
{
    public static async Task RequestAsync(IReadOnlyCollection<Process> processes,CancellationToken ct)
    {
        if(processes.Count==0) return;
        ct.ThrowIfCancellationRequested();
        var entries=processes.Select(p=>new UniqueProcess
        {Id=(uint)p.Id,Start=ToFileTime(p.StartTime.ToUniversalTime().ToFileTimeUtc())}).ToArray();
        uint handle=0;
        if(RmStartSession(out handle,0,new StringBuilder(33))!=0) throw new UserFault("Windows не смогла запросить выход из Codex. Выберите «Выход» в меню Codex.");
        try
        {
            if(RmRegisterResources(handle,0,null,(uint)entries.Length,entries,0,null)!=0)
                throw new UserFault("Не удалось проверить приложение для завершения. Выберите «Выход» в меню Codex.");
            using var registration=ct.Register(()=>RmCancelCurrentTask(handle));
            var result=await Task.Run(()=>RmShutdown(handle,0,IntPtr.Zero),CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if(result!=0) throw new UserFault("Codex не завершился. Завершите активные задачи и выберите «Выход» в его меню или трее.");
        }
        finally {RmEndSession(handle);}
    }
    private static System.Runtime.InteropServices.ComTypes.FILETIME ToFileTime(long value)=>new() {dwLowDateTime=(int)value,dwHighDateTime=(int)(value>>32)};
    [StructLayout(LayoutKind.Sequential)] private struct UniqueProcess
    {public uint Id;public System.Runtime.InteropServices.ComTypes.FILETIME Start;}
    [DllImport("rstrtmgr.dll",CharSet=CharSet.Unicode)] private static extern uint RmStartSession(out uint handle,uint flags,StringBuilder key);
    [DllImport("rstrtmgr.dll",CharSet=CharSet.Unicode)] private static extern uint RmRegisterResources(uint handle,uint nFiles,string[]? files,uint nApps,UniqueProcess[] apps,uint nServices,string[]? services);
    [DllImport("rstrtmgr.dll")] private static extern uint RmShutdown(uint handle,uint flags,IntPtr callback);
    [DllImport("rstrtmgr.dll")] private static extern uint RmCancelCurrentTask(uint handle);
    [DllImport("rstrtmgr.dll")] private static extern uint RmEndSession(uint handle);
}
