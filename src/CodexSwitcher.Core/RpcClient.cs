using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CodexSwitcher.Core;

public sealed class RpcClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentDictionary<long,TaskCompletionSource<JsonElement>> pending=new();
    private readonly Channel<JsonElement> logins=Channel.CreateUnbounded<JsonElement>();
    private readonly SemaphoreSlim writer=new(1,1);
    private readonly Task reader,stderr;
    private long nextId;
    private int disposed;
    private RpcClient(Process process)
    {
        this.process=process;
        reader=ReadAsync();
        // Never retain or display app-server stderr: errors may contain authentication data.
        stderr=DiscardErrorsAsync();
    }
    public static async Task<RpcClient> StartAsync(ProcessStartInfo startInfo,CancellationToken ct)
    {
        startInfo.UseShellExecute=false; startInfo.CreateNoWindow=true;
        startInfo.RedirectStandardInput=true; startInfo.RedirectStandardOutput=true; startInfo.RedirectStandardError=true;
        var process=Process.Start(startInfo) ?? throw new UserFault("Не удалось запустить службу входа Codex.");
        var client=new RpcClient(process);
        try
        {
            await client.RequestAsync("initialize",new
            {clientInfo=new {name="codex_switcher",title="Codex Switcher",version="1.6.0"},capabilities=new {experimentalApi=false}},ct);
            await client.SendAsync(new {method="initialized",@params=new {}},ct);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }
    public async Task<JsonElement> RequestAsync(string method,object parameters,CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed!=0,this);
        var id=Interlocked.Increment(ref nextId);
        var source=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(process.HasExited) throw new UserFault("Служба входа Codex завершилась. Повторите операцию.");
        pending[id]=source;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await SendAsync(new {id,method,@params=parameters},deadline.Token);
            return await source.Task.WaitAsync(deadline.Token);
        }
        finally { pending.TryRemove(id,out _); }
    }
    private async Task SendAsync(object message,CancellationToken ct)
    {
        await writer.WaitAsync(ct);
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(),ct); await process.StandardInput.FlushAsync(ct); }
        finally { writer.Release(); }
    }
    private async Task ReadAsync()
    {
        try
        {
            while(await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if(line.Length>2_000_000) throw new UserFault("Неподдерживаемый ответ службы Codex.");
                using var doc=JsonDocument.Parse(line); var root=doc.RootElement;
                if(root.TryGetProperty("id",out var id) && id.TryGetInt64(out var key) && pending.TryRemove(key,out var source))
                {
                    if(root.TryGetProperty("error",out _)) source.TrySetException(new UserFault("Codex отклонил запрос входа. Проверьте соединение или повторите вход."));
                    else if(root.TryGetProperty("result",out var result)) source.TrySetResult(result.Clone());
                    else source.TrySetException(new UserFault("Неподдерживаемый ответ Codex."));
                }
                else if(root.TryGetProperty("method",out var method) && method.GetString()=="account/login/completed")
                    await logins.Writer.WriteAsync(root.GetProperty("params").Clone());
            }
        }
        catch { /* Deliver only a fixed safe error, never raw RPC text. */ }
        finally
        {
            foreach(var entry in pending) if(pending.TryRemove(entry.Key,out var source))
                source.TrySetException(new UserFault("Служба входа Codex завершилась. Повторите операцию."));
            logins.Writer.TryComplete();
        }
    }
    public async Task WaitLoginAsync(string loginId,CancellationToken ct)
    {
        try
        {
            await foreach(var notification in logins.Reader.ReadAllAsync(ct))
                if(notification.GetProperty("loginId").GetString()==loginId)
                {
                    if(!notification.GetProperty("success").GetBoolean()) throw new UserFault("Вход не завершён. Повторите вход в браузере.");
                    return;
                }
            throw new UserFault("Служба входа закрылась до завершения входа.");
        }
        catch(ChannelClosedException) { throw new UserFault("Служба входа закрылась до завершения входа."); }
    }
    private async Task DiscardErrorsAsync()
    { try { var buffer=new char[4096]; while(await process.StandardError.ReadAsync(buffer)>0) { } } catch { } }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0) return;
        try
        {
            process.StandardInput.Close();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch(OperationCanceledException) { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
            await Task.WhenAll(reader,stderr);
        }
        finally { process.Dispose(); writer.Dispose(); }
    }
}



