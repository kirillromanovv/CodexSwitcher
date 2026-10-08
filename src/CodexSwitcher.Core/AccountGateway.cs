using System.Diagnostics;
using System.Text.Json;

namespace CodexSwitcher.Core;

public sealed class AccountGateway(string cliPath,string stagingRoot) : IAccountGateway
{
    public async Task<LimitsReadResult> ReadLimitsAsync(string authJson,CancellationToken cancellationToken)
    {
        var expected=AuthDocument.Parse(authJson).Identity;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await InIsolatedHome(async (home,client,ct)=>
        {
            var account=await client.RequestAsync("account/read",new {refreshToken=false},ct);
            CheckAccount(account,expected.Email);
            var response=await client.RequestAsync("account/rateLimits/read",new {},ct);
            var refreshed=File.ReadAllText(Path.Combine(home,"auth.json"));
            if(AuthDocument.Parse(refreshed).Identity.Key!=expected.Key) throw new UserFault("Проверка лимитов вернула другой аккаунт.");
            var details=account.GetProperty("account");
            var plan=details.TryGetProperty("planType",out var p) && p.ValueKind==JsonValueKind.String?p.GetString():null;
            return new LimitsReadResult(AccountLimits.Parse(response,plan),refreshed);
        },authJson,timeout.Token);
    }
    public async Task<string> VerifyAsync(string authJson,CancellationToken cancellationToken)
    {
        var expected=AuthDocument.Parse(authJson).Identity;
        return await InIsolatedHome(async (home,client,ct)=>
        {
            var response=await client.RequestAsync("account/read",new {refreshToken=true},ct);
            CheckAccount(response,expected.Email);
            var refreshed=File.ReadAllText(Path.Combine(home,"auth.json"));
            if(AuthDocument.Parse(refreshed).Identity.Key!=expected.Key) throw new UserFault("Codex подтвердил другой аккаунт. Повторите вход.");
            return refreshed;
        },authJson,cancellationToken);
    }
    public async Task<string> LoginAsync(Action<string> openUrl,CancellationToken cancellationToken)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        return await InIsolatedHome(async (home,client,ct)=>
        {
            string? loginId=null;
            try
            {
                var result=await client.RequestAsync("account/login/start",new {type="chatgpt",appBrand="codex"},ct);
                loginId=result.GetProperty("loginId").GetString()!;
                var url=result.GetProperty("authUrl").GetString()!;
                if(!IsOfficialLoginUrl(url)) throw new UserFault("Codex вернул неизвестный адрес входа. Операция остановлена.");
                openUrl(url);
                await client.WaitLoginAsync(loginId,ct);
                var response=await client.RequestAsync("account/read",new {refreshToken=true},ct);
                var auth=File.ReadAllText(Path.Combine(home,"auth.json"));
                CheckAccount(response,AuthDocument.Parse(auth).Identity.Email);
                return auth;
            }
            catch(OperationCanceledException)
            {
                if(loginId!=null)
                {
                    using var cancelTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await client.RequestAsync("account/login/cancel",new {loginId},cancelTimeout.Token); } catch { }
                }
                throw;
            }
        },null,timeout.Token);
    }
    public static bool IsOfficialLoginUrl(string url) => Uri.TryCreate(url,UriKind.Absolute,out var uri) &&
        uri.Scheme=="https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Host is "auth.openai.com" or "auth0.openai.com" or "auth.chatgpt.com" or "chatgpt.com";
    public static void CheckAccount(JsonElement response,string email)
    {
        if(!response.TryGetProperty("account",out var account) || account.ValueKind!=JsonValueKind.Object ||
            account.GetProperty("type").GetString()!="chatgpt" ||
            !string.Equals(account.GetProperty("email").GetString(),email,StringComparison.OrdinalIgnoreCase))
            throw new UserFault("Не удалось подтвердить вход через ChatGPT. Повторите вход.");
    }
    public static ProcessStartInfo StartInfo(string cli,string home)
    {
        var info=new ProcessStartInfo(cli) {WorkingDirectory=home};
        info.ArgumentList.Add("app-server"); info.ArgumentList.Add("--listen"); info.ArgumentList.Add("stdio://");
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        info.Environment["CODEX_HOME"]=home;
        // Isolated login must not inherit an API key, access token, remote endpoint or provider override.
        foreach(var name in info.Environment.Keys.ToArray())
            if(name.StartsWith("CODEX_",StringComparison.OrdinalIgnoreCase) || name.StartsWith("OPENAI_",StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CHATGPT_",StringComparison.OrdinalIgnoreCase) || name is "ACCESS_TOKEN") info.Environment.Remove(name);
        info.Environment["CODEX_HOME"]=home;
        info.Environment["RUST_LOG"]="off";
        return info;
    }
    private async Task<T> InIsolatedHome<T>(Func<string,RpcClient,CancellationToken,Task<T>> run,string? auth,CancellationToken ct)
    {
        SecureFiles.PrivateDirectory(stagingRoot);
        var home=Path.Combine(stagingRoot,Guid.NewGuid().ToString("N")); SecureFiles.PrivateDirectory(home);
        try
        {
            SecureFiles.AtomicText(Path.Combine(home,"config.toml"),"cli_auth_credentials_store = \"file\"\n");
            if(auth!=null) SecureFiles.AtomicText(Path.Combine(home,"auth.json"),auth);
            await using var client=await RpcClient.StartAsync(StartInfo(cliPath,home),ct);
            return await run(home,client,ct);
        }
        finally
        {
            // Only this operation's generated child directory can be removed.
            SecureFiles.RejectLinks(home);
            if(Directory.Exists(home)) Directory.Delete(home,true);
        }
    }
}
