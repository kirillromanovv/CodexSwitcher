using System.Text;
using System.Text.Json;
using CodexSwitcher.Core;
using System.Diagnostics;

if(args.Length>=2 && args[0]=="--tray-fixture")
{
    var fixtureThread=new Thread(()=>System.Windows.Forms.Application.Run(new TrayFixture(args[1],args.Contains("--refuse"))));
    fixtureThread.SetApartmentState(ApartmentState.STA);fixtureThread.Start();fixtureThread.Join();return 0;
}
if (args.Contains("--rpc-fixture") || args.FirstOrDefault()=="app-server")
{
    while (Console.ReadLine() is { } line)
    {
        using var request=JsonDocument.Parse(line); var root=request.RootElement;
        if (!root.TryGetProperty("id",out var id)) continue;
        var method=root.GetProperty("method").GetString();
        if(method=="account/login/start")
        {
            Console.WriteLine("{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"test-login\",\"success\":true}}");
            Console.WriteLine(JsonSerializer.Serialize(new { id=id.GetInt64(), result=new { loginId="test-login" }}));
        }
        else if(method=="account/read")
        {
            var home=Environment.GetEnvironmentVariable("CODEX_HOME");var auth=home==null?null:Path.Combine(home,"auth.json");
            var email=auth!=null&&File.Exists(auth)?AuthDocument.Parse(File.ReadAllText(auth)).Identity.Email:"test@example.invalid";
            Console.WriteLine(JsonSerializer.Serialize(new {id=id.GetInt64(),result=new {account=new {type="chatgpt",email,planType="pro"}}}));
        }
        else if(method=="account/rateLimits/read") Console.WriteLine(JsonSerializer.Serialize(new {id=id.GetInt64(),result=new {rateLimits=new {limitId="codex",primary=new {usedPercent=15,windowDurationMins=300,resetsAt=1800000000},secondary=new {usedPercent=40,windowDurationMins=10080,resetsAt=1800000000}},rateLimitResetCredits=new {availableCount=4}}}));
        else if(method=="test/hang") continue;
        else if(method=="test/error") Console.WriteLine(JsonSerializer.Serialize(new { id=id.GetInt64(), error=new { code=-1,message="DO-NOT-EXPOSE-synthetic-token" } }));
        else Console.WriteLine(JsonSerializer.Serialize(new { id=id.GetInt64(),result=new { }}));
    }
    return 0;
}
if(args.Length==2 && args[0]=="--probe")
{
    // Real binary/protocol smoke. No current CODEX_HOME or real credentials are opened.
    var path=Path.GetFullPath(args[1]);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var home=Path.Combine(Path.GetDirectoryName(path)!,"probe-"+Guid.NewGuid().ToString("N"));
    SecureFiles.PrivateDirectory(home);
    try
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var package=await StorePackage.FindAsync(timeout.Token);
        await using var client=await RpcClient.StartAsync(AccountGateway.StartInfo(package.Cli,home),timeout.Token);
        var response=await client.RequestAsync("account/read",new {refreshToken=false},timeout.Token);
        var signedOut=!response.TryGetProperty("account",out var account)||account.ValueKind==JsonValueKind.Null;
        File.WriteAllText(path,JsonSerializer.Serialize(new {StorePackage=package.FamilyName,Aumid=package.Aumid,Executable=package.Executable,ProtocolInitialized=true,IsolatedHomeSignedOut=signedOut},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(signedOut?"PASS installed Codex protocol in isolated signed-out home":"FAIL isolated home unexpectedly signed in");
        return signedOut?0:1;
    }
    finally {SecureFiles.RejectLinks(home);Directory.Delete(home,true);}
}
if(args.Length==2 && args[0]=="--limits-probe")
{
    // Explicit read-only live acceptance: no switching, closing, generation or reset redemption.
    var output=Path.GetFullPath(args[1]);var vault=new Vault(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Codex Switcher"));
    var home=Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
    var authPath=Path.Combine(home,"auth.json");var before=File.ReadAllBytes(authPath);
    var package=await StorePackage.FindAsync();var gateway=new AccountGateway(package.Cli,Path.Combine(vault.Root,"staging"));
    var rows=new List<object>();
    foreach(var slot in Enum.GetValues<Slot>())
    {
        var profile=vault.Load(slot);if(profile==null)continue;
        try
        {
            var result=await gateway.ReadLimitsAsync(profile.AuthJson,CancellationToken.None);
            rows.Add(new {Account=(int)slot+1,Success=true,result.Limits.FiveHour,result.Limits.Weekly,result.Limits.AvailableResets,result.Limits.Plan,result.Limits.FetchedAt,
                ResetExpirations=result.Limits.ResetCredits?.Select((credit,index)=>new {Number=index+1,credit.ExpiresAt,credit.HasExpirationInfo,credit.GrantedAt}).ToArray()});
        }
        catch {rows.Add(new {Account=(int)slot+1,Success=false});}
    }
    var unchanged=before.SequenceEqual(File.ReadAllBytes(authPath));
    File.WriteAllText(output,JsonSerializer.Serialize(new {ActiveAuthUnchanged=unchanged,Accounts=rows},new JsonSerializerOptions {WriteIndented=true}));
    Console.WriteLine($"Read {rows.Count} saved accounts; active auth unchanged: {unchanged}. Safe metrics written to report.");
    return unchanged?0:1;
}

var failures = 0;
var count = 0;
async Task Test(string name, Func<Task> run)
{
    count++;
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + " [" + e.GetType().Name + $" HRESULT={e.HResult:X8}]"); Console.WriteLine(e.StackTrace); }
}
void Assert(bool ok) { if (!ok) throw new Exception("Assertion failed"); }
async Task Throws(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected failure"); }
string Auth(string user, string account = "workspace-a", string refresh = "synthetic-refresh")
{
    string Enc(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+','-').Replace('/','_');
    var jwt = Enc("{\"alg\":\"none\"}") + "." + Enc(JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["sub"] = user, ["email"] = user + "@example.invalid",
        ["https://api.openai.com/auth"] = new { chatgpt_account_id = account }
    })) + ".synthetic";
    return JsonSerializer.Serialize(new { auth_mode = "chatgpt", tokens = new { id_token = jwt, access_token = "synthetic-access", refresh_token = refresh, account_id = account } });
}
string Temp() { var path=Path.Combine(AppContext.BaseDirectory,"sandbox",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
await Test("five account slots preserve existing numeric identities",()=>
{
    var slots=Enum.GetValues<Slot>();Assert(slots.Length==5 && (int)slots[0]==0 && (int)slots[1]==1);
    return Task.CompletedTask;
});
await Test("identity includes workspace", () => { Assert(AuthDocument.Parse(Auth("u1")).Identity.Key != AuthDocument.Parse(Auth("u1","workspace-b")).Identity.Key); return Task.CompletedTask; });
await Test("empty slots can be removed including defaults and stay removed after restart",()=>
{
    var root=Temp();var vault=new Vault(root);var extra=vault.AddSlot();vault.RemoveEmptySlot(extra);
    Assert(!new Vault(root).Slots().Contains(extra));
    vault.RemoveEmptySlot(Slot.Work);vault.RemoveEmptySlot(Slot.Personal);
    Assert(new Vault(root).Slots().Count==0);Assert(vault.AddSlot()==Slot.Work);
    return Task.CompletedTask;
});
await Test("empty-slot removal refuses saved and damaged credentials",async ()=>
{
    var root=Temp();var vault=new Vault(root);vault.Save(new(Slot.Work,Auth("protected"),DateTimeOffset.UtcNow));
    var path=Path.Combine(root,"work.dpapi");var before=File.ReadAllBytes(path);
    await Throws(()=>{vault.RemoveEmptySlot(Slot.Work);return Task.CompletedTask;});Assert(before.SequenceEqual(File.ReadAllBytes(path)));
    File.WriteAllText(Path.Combine(root,"personal.dpapi"),"damaged");
    await Throws(()=>{vault.RemoveEmptySlot(Slot.Personal);return Task.CompletedTask;});Assert(File.ReadAllText(Path.Combine(root,"personal.dpapi"))=="damaged");
});
await Test("account list starts with two and persists additions beyond five",()=>
{
    var root=Temp();var vault=new Vault(root);Assert(vault.Slots().SequenceEqual(new[]{Slot.Work,Slot.Personal}));
    for(var number=3;number<=12;number++)Assert((int)vault.AddSlot()==number-1);
    Assert(new Vault(root).Slots().Count==12);
    return Task.CompletedTask;
});
await Test("legacy profiles stay visible without an account list",()=>
{
    var vault=new Vault(Temp());vault.Save(new(Slot.Account5,Auth("legacy"),DateTimeOffset.UtcNow));
    Assert(vault.Slots().Contains(Slot.Account5));Assert((int)vault.AddSlot()==5);
    return Task.CompletedTask;
});
await Test("dynamic account switches and preserves latest current tokens",async ()=>
{
    var root=Temp();var vault=new Vault(root);var current=(Slot)7;var target=(Slot)11;
    vault.Save(new(current,Auth("dynamic-current",refresh:"old"),DateTimeOffset.UtcNow));vault.Save(new(target,Auth("dynamic-target"),DateTimeOffset.UtcNow));
    var path=Path.Combine(root,"auth.json");File.WriteAllText(path,Auth("dynamic-current",refresh:"latest"));
    await new Switcher(vault,path,new FakeGateway(),new FakeApp()).SwitchAsync(target);
    Assert(vault.Load(current)!.AuthJson.Contains("latest")&&AuthDocument.Parse(File.ReadAllText(path)).Identity.Subject=="dynamic-target");
});
await Test("reject damaged auth", () => Throws(() => { AuthDocument.Parse("{broken"); return Task.CompletedTask; }));
await Test("reject API key auth", () => Throws(() => { AuthDocument.Parse("{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"fake\"}"); return Task.CompletedTask; }));
await Test("vault encrypted roundtrip", () =>
{
    var v=new Vault(Temp()); var json=Auth("u1"); v.Save(new(Slot.Work,json,DateTimeOffset.UtcNow));
    Assert(v.Load(Slot.Work)?.AuthJson == json);
    Assert(Directory.GetFiles(v.Root).All(p=>!Encoding.UTF8.GetString(File.ReadAllBytes(p)).Contains("synthetic-refresh")));
    return Task.CompletedTask;
});
await Test("corrupt vault rejected", () =>
{
    var v=new Vault(Temp()); v.Save(new(Slot.Work,Auth("u1"),DateTimeOffset.UtcNow));
    var file=Directory.GetFiles(v.Root).Single(); var bytes=File.ReadAllBytes(file); bytes[bytes.Length/2]^=127; File.WriteAllBytes(file,bytes);
    return Throws(()=>{v.Load(Slot.Work); return Task.CompletedTask;});
});
await Test("switch preserves latest current tokens and other files", async () =>
{
    var root=Temp(); var v=new Vault(Path.Combine(root,"vault")); var active=Path.Combine(root,"auth.json");
    v.Save(new(Slot.Work,Auth("u1",refresh:"old"),DateTimeOffset.UtcNow)); v.Save(new(Slot.Personal,Auth("u2"),DateTimeOffset.UtcNow));
    File.WriteAllText(active,Auth("u1",refresh:"new")); File.WriteAllText(Path.Combine(root,"config.toml"),"untouched");
    var app=new FakeApp(); var sw=new Switcher(v,active,new FakeGateway(),app);
    var result=await sw.SwitchAsync(Slot.Personal);
    Assert(AuthDocument.Parse(File.ReadAllText(active)).Identity.Subject=="u2");
    Assert(v.Load(Slot.Work)!.AuthJson.Contains("new")); Assert(!v.HasRecovery && result.LaunchSucceeded && app.Launched);
    Assert(File.ReadAllText(Path.Combine(root,"config.toml"))=="untouched");
    await sw.SwitchAsync(Slot.Work); Assert(AuthDocument.Parse(File.ReadAllText(active)).Identity.Subject=="u1");
});
await Test("unknown current account blocks switch", async () =>
{
    var root=Temp(); var v=new Vault(Path.Combine(root,"vault")); var active=Path.Combine(root,"auth.json"); var original=Auth("unknown"); File.WriteAllText(active,original);
    v.Save(new(Slot.Personal,Auth("u2"),DateTimeOffset.UtcNow)); var app=new FakeApp();
    await Throws(()=>new Switcher(v,active,new FakeGateway(),app).SwitchAsync(Slot.Personal));
    Assert(File.ReadAllText(active)==original && !app.Closed);
});
await Test("busy app leaves active auth unchanged", async () =>
{
    var root=Temp(); var v=new Vault(Path.Combine(root,"vault")); var active=Path.Combine(root,"auth.json"); var original=Auth("u1"); File.WriteAllText(active,original);
    v.Save(new(Slot.Work,original,DateTimeOffset.UtcNow)); v.Save(new(Slot.Personal,Auth("u2"),DateTimeOffset.UtcNow));
    await Throws(()=>new Switcher(v,active,new FakeGateway(),new FakeApp{CloseFails=true}).SwitchAsync(Slot.Personal));
    Assert(File.ReadAllText(active)==original && !v.HasRecovery);
});
await Test("verification failure restores prior auth", async () =>
{
    var root=Temp(); var v=new Vault(Path.Combine(root,"vault")); var active=Path.Combine(root,"auth.json"); var original=Auth("u1"); File.WriteAllText(active,original);
    v.Save(new(Slot.Work,original,DateTimeOffset.UtcNow)); v.Save(new(Slot.Personal,Auth("u2"),DateTimeOffset.UtcNow)); var app=new FakeApp();
    await Throws(()=>new Switcher(v,active,new FakeGateway{FailCall=2},app).SwitchAsync(Slot.Personal));
    Assert(File.ReadAllText(active)==original && !v.HasRecovery && app.Launched);
});
await Test("target identity mismatch never closes app", async () =>
{
    var root=Temp(); var v=new Vault(Path.Combine(root,"vault")); var active=Path.Combine(root,"auth.json"); var original=Auth("u1"); File.WriteAllText(active,original);
    v.Save(new(Slot.Work,original,DateTimeOffset.UtcNow)); v.Save(new(Slot.Personal,Auth("u2"),DateTimeOffset.UtcNow)); var app=new FakeApp();
    await Throws(()=>new Switcher(v,active,new FakeGateway{ReturnAuth=Auth("imposter")},app).SwitchAsync(Slot.Personal));
    Assert(File.ReadAllText(active)==original && !app.Closed);
});
await Test("launch failure reports committed account", async () =>
{
    var root=Temp(); var v=new Vault(Path.Combine(root,"vault")); var active=Path.Combine(root,"auth.json"); File.WriteAllText(active,Auth("u1"));
    v.Save(new(Slot.Work,Auth("u1"),DateTimeOffset.UtcNow)); v.Save(new(Slot.Personal,Auth("u2"),DateTimeOffset.UtcNow));
    var result=await new Switcher(v,active,new FakeGateway(),new FakeApp{LaunchFails=true}).SwitchAsync(Slot.Personal);
    Assert(!result.LaunchSucceeded && AuthDocument.Parse(File.ReadAllText(active)).Identity.Subject=="u2" && !v.HasRecovery);
});
await Test("atomic writes tolerate short file-reader locks",async ()=>
{
    var path=Path.Combine(Temp(),"atomic.txt"); File.WriteAllText(path,"old");
    var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
    var release=Task.Run(async ()=>{await Task.Delay(150); stream.Dispose();});
    await Task.Run(()=>SecureFiles.AtomicText(path,"new")); await release;
    Assert(File.ReadAllText(path)=="new");
});
await Test("RPC handles response and early login notification",async ()=>
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var info=new ProcessStartInfo(Environment.ProcessPath!); info.ArgumentList.Add("--rpc-fixture");
    await using var client=await RpcClient.StartAsync(info,timeout.Token);
    var login=await client.RequestAsync("account/login/start",new { type="chatgpt" },timeout.Token);
    await client.WaitLoginAsync(login.GetProperty("loginId").GetString()!,timeout.Token);
    var account=await client.RequestAsync("account/read",new {refreshToken=false},timeout.Token);
    Assert(account.GetProperty("account").GetProperty("type").GetString()=="chatgpt");
});
await Test("RPC errors hide server token text",async ()=>
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var info=new ProcessStartInfo(Environment.ProcessPath!); info.ArgumentList.Add("--rpc-fixture");
    await using var client=await RpcClient.StartAsync(info,timeout.Token);
    try { await client.RequestAsync("test/error",new {},timeout.Token); throw new Exception("Missing error"); }
    catch(UserFault e) { Assert(!e.ToString().Contains("DO-NOT-EXPOSE")); }
});
await Test("RPC cancellation is bounded",async ()=>
{
    var info=new ProcessStartInfo(Environment.ProcessPath!);info.ArgumentList.Add("--rpc-fixture");
    await using var client=await RpcClient.StartAsync(info,CancellationToken.None);
    using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    await Throws(()=>client.RequestAsync("test/hang",new {},cancel.Token));
    var response=await client.RequestAsync("account/read",new {refreshToken=false},CancellationToken.None);
    Assert(response.GetProperty("account").GetProperty("type").GetString()=="chatgpt");
});
await Test("recovery restores journaled original",async ()=>
{
    var root=Temp();var v=new Vault(Path.Combine(root,"vault"));var path=Path.Combine(root,"auth.json");var original=Auth("u1");var target=Auth("u2");
    v.BeginRecovery(new RecoveryRecord(Path.GetFullPath(path),original,AuthDocument.Parse(target).Identity.Key,Slot.Personal));File.WriteAllText(path,target);
    var app=new FakeApp();var result=await new Switcher(v,path,new FakeGateway(),app).RecoverAsync();
    Assert(File.ReadAllText(path)==original && !v.HasRecovery && result.LaunchSucceeded);
});
await Test("recovery preserves an externally changed account",async ()=>
{
    var root=Temp();var v=new Vault(Path.Combine(root,"vault"));var path=Path.Combine(root,"auth.json");var external=Auth("u3");
    v.BeginRecovery(new RecoveryRecord(Path.GetFullPath(path),Auth("u1"),AuthDocument.Parse(Auth("u2")).Identity.Key,Slot.Personal));File.WriteAllText(path,external);
    await Throws(()=>new Switcher(v,path,new FakeGateway(),new FakeApp()).RecoverAsync());Assert(File.ReadAllText(path)==external && v.HasRecovery);
});
await Test("recovery restores an originally signed-out home",async ()=>
{
    var root=Temp();var v=new Vault(Path.Combine(root,"vault"));var path=Path.Combine(root,"auth.json");var target=Auth("u2");
    v.BeginRecovery(new RecoveryRecord(Path.GetFullPath(path),null,AuthDocument.Parse(target).Identity.Key,Slot.Personal));File.WriteAllText(path,target);
    await new Switcher(v,path,new FakeGateway(),new FakeApp()).RecoverAsync();Assert(!File.Exists(path)&&!v.HasRecovery);
});
await Test("recovery rejects a different destination",async ()=>
{
    var root=Temp();var v=new Vault(Path.Combine(root,"vault"));var path=Path.Combine(root,"auth.json");var original=Auth("u1");
    v.BeginRecovery(new RecoveryRecord(Path.Combine(root,"other.json"),original,AuthDocument.Parse(Auth("u2")).Identity.Key,Slot.Personal));File.WriteAllText(path,original);
    await Throws(()=>new Switcher(v,path,new FakeGateway(),new FakeApp()).RecoverAsync());Assert(File.ReadAllText(path)==original && v.HasRecovery);
});
await Test("pending recovery blocks a new switch",async ()=>
{
    var root=Temp();var v=new Vault(Path.Combine(root,"vault"));var path=Path.Combine(root,"auth.json");var original=Auth("u1");
    v.BeginRecovery(new RecoveryRecord(Path.GetFullPath(path),original,AuthDocument.Parse(Auth("u2")).Identity.Key,Slot.Personal));File.WriteAllText(path,original);
    var app=new FakeApp();await Throws(()=>new Switcher(v,path,new FakeGateway(),app).SwitchAsync(Slot.Personal));Assert(!app.Closed && v.HasRecovery);
});
await Test("home policy rejects OS credential storage without edits",async ()=>
{
    var home=Temp();var path=Path.Combine(home,"config.toml");var config="cli_auth_credentials_store = 'keyring'\n";File.WriteAllText(path,config);
    try {HomePolicy.Check(home);throw new Exception("Expected UserFault");} catch(UserFault) { }
    Assert(File.ReadAllText(path)==config);await Task.CompletedTask;
});
await Test("home policy enforces workspace restriction",()=>
{
    var home=Temp();File.WriteAllText(Path.Combine(home,"config.toml"),"forced_chatgpt_workspace_id = \"workspace-a\"\n");
    HomePolicy.Check(home,AuthDocument.Parse(Auth("u1")).Identity);
    return Throws(()=>{HomePolicy.Check(home,AuthDocument.Parse(Auth("u2","workspace-b")).Identity);return Task.CompletedTask;});
});
await Test("home policy rejects escaped quoted credential keys",()=>
{
    var home=Temp();File.WriteAllText(Path.Combine(home,"config.toml"),"\"cli_auth_credentials\\u005fstore\" = \"keyring\"\n");
    return Throws(()=>{HomePolicy.Check(home);return Task.CompletedTask;});
});
await Test("home policy rejects escaped and dotted workspace keys",async ()=>
{
    var home=Temp();var path=Path.Combine(home,"config.toml");
    File.WriteAllText(path,"\"forced_chatgpt\\u005fworkspace_id\" = \"workspace-a\"\n");
    await Throws(()=>{HomePolicy.Check(home,AuthDocument.Parse(Auth("u2","workspace-b")).Identity);return Task.CompletedTask;});
    File.WriteAllText(path,"profiles.work.forced_chatgpt_workspace_id = \"workspace-a\"\n");
    await Throws(()=>{HomePolicy.Check(home,AuthDocument.Parse(Auth("u2","workspace-b")).Identity);return Task.CompletedTask;});
    File.WriteAllText(path,"profiles.work.\"forced_chatgpt\\u005fworkspace_id\" = \"workspace-a\"\n");
    await Throws(()=>{HomePolicy.Check(home,AuthDocument.Parse(Auth("u2","workspace-b")).Identity);return Task.CompletedTask;});
});
await Test("already-selected account still reports launch failure honestly",async ()=>
{
    var root=Temp();var v=new Vault(Path.Combine(root,"vault"));var path=Path.Combine(root,"auth.json");var original=Auth("u1");
    v.Save(new(Slot.Work,original,DateTimeOffset.UtcNow));File.WriteAllText(path,original);var app=new FakeApp{LaunchFails=true};
    var result=await new Switcher(v,path,new FakeGateway(),app).SwitchAsync(Slot.Work);
    Assert(!result.LaunchSucceeded&&!app.Closed&&File.ReadAllText(path)==original);
});
await Test("failed rollback retains encrypted journal and retry restores",async ()=>
{
    var root=Temp();var v=new Vault(Path.Combine(root,"vault"));var path=Path.Combine(root,"auth.json");var original=Auth("u1");File.WriteAllText(path,original);
    v.Save(new(Slot.Work,original,DateTimeOffset.UtcNow));v.Save(new(Slot.Personal,Auth("u2"),DateTimeOffset.UtcNow));
    FileStream? held=null;var gateway=new FakeGateway{FailCall=2,OnVerify=call=>{if(call==2)held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);}};
    var engine=new Switcher(v,path,gateway,new FakeApp());
    try {await Throws(()=>engine.SwitchAsync(Slot.Personal));Assert(v.HasRecovery);Assert(AuthDocument.Parse(File.ReadAllText(path)).Identity.Subject=="u2");}
    finally {held?.Dispose();}
    await engine.RecoverAsync();Assert(File.ReadAllText(path)==original&&!v.HasRecovery);
});
await Test("login URL rejects phishing and local endpoints",()=>
{
    Assert(AccountGateway.IsOfficialLoginUrl("https://auth.openai.com/oauth/authorize?test=1"));
    Assert(!AccountGateway.IsOfficialLoginUrl("https://auth.openai.com.evil.invalid/a"));
    Assert(!AccountGateway.IsOfficialLoginUrl("https://user@auth.openai.com/a"));
    Assert(!AccountGateway.IsOfficialLoginUrl("http://localhost/a"));
    return Task.CompletedTask;
});
await Test("isolated server removes credential environment and preserves argv",()=>
{
    var old=Environment.GetEnvironmentVariable("OPENAI_API_KEY");try
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY","synthetic-secret");
        var home=Path.Combine(Temp(),"space and $characters");var info=AccountGateway.StartInfo("codex.exe",home);
        Assert(!info.Environment.ContainsKey("OPENAI_API_KEY") && info.Environment["CODEX_HOME"]==home && info.ArgumentList[0]=="app-server");
        Assert(info.ArgumentList.Contains("cli_auth_credentials_store=\"file\""));
    }
    finally {Environment.SetEnvironmentVariable("OPENAI_API_KEY",old);}return Task.CompletedTask;
});
await Test("reset expiry details survive quota parsing",()=>
{
    using var response=JsonDocument.Parse("""{"rateLimitResetCredits":{"availableCount":3,"credits":[{"id":"later","status":"available","expiresAt":1800000200,"grantedAt":1790000000,"title":"Reset"},{"id":"earlier","status":"available","expiresAt":1800000100,"grantedAt":1790000000,"title":null}]}}""");
    using var parsed=JsonDocument.Parse(JsonSerializer.Serialize(AccountLimits.Parse(response.RootElement)));
    Assert(parsed.RootElement.TryGetProperty("ResetCredits",out var details)&&details.GetArrayLength()==2);
    Assert(details[0].GetProperty("Id").GetString()=="earlier"&&details[0].GetProperty("ExpiresAt").GetDateTimeOffset()==DateTimeOffset.FromUnixTimeSeconds(1800000100));
    Assert(parsed.RootElement.GetProperty("AvailableResets").GetInt32()==3);
    return Task.CompletedTask;
});
await Test("reset details distinguish unavailable list from known empty list",()=>
{
    using var unknown=JsonDocument.Parse("""{"rateLimitResetCredits":{"availableCount":2,"credits":null}}""");
    using var empty=JsonDocument.Parse("""{"rateLimitResetCredits":{"availableCount":0,"credits":[]}}""");
    Assert(AccountLimits.Parse(unknown.RootElement).ResetCredits==null);
    Assert(AccountLimits.Parse(empty.RootElement).ResetCredits is {Count:0});return Task.CompletedTask;
});
await Test("reset expiration distinguishes no expiry from missing or invalid date",()=>
{
    using var json=JsonDocument.Parse("""{"rateLimitResetCredits":{"availableCount":3,"credits":[{"id":"unlimited","expiresAt":null},{"id":"missing"},{"id":"broken","expiresAt":9223372036854775807}]}}""");
    var rows=AccountLimits.Parse(json.RootElement).ResetCredits!;
    Assert(rows.Count==3&&rows.Single(c=>c.Id=="unlimited").HasExpirationInfo);
    Assert(!rows.Single(c=>c.Id=="missing").HasExpirationInfo&&!rows.Single(c=>c.Id=="broken").HasExpirationInfo);return Task.CompletedTask;
});
await Test("reset details discard redeemed entries and duplicates without changing authoritative total",()=>
{
    using var json=JsonDocument.Parse("""{"rateLimitResetCredits":{"availableCount":4,"credits":[{"id":"a","status":"available","expiresAt":1800000000},{"id":"a","status":"available","expiresAt":1800000000},{"id":"b","status":"redeemed","expiresAt":1800000000}]}}""");
    var limits=AccountLimits.Parse(json.RootElement);Assert(limits.AvailableResets==4&&limits.ResetCredits is {Count:1});return Task.CompletedTask;
});
await Test("limits prefer codex bucket and match durations even when reversed",()=>
{
    using var json=JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":300}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":80,"windowDurationMins":10080,"resetsAt":1800000000},"secondary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":1800000100}},"other":{"primary":{"usedPercent":0,"windowDurationMins":300}}},"rateLimitResetCredits":{"availableCount":3,"credits":[]}}""");
    var limits=AccountLimits.Parse(json.RootElement,"pro");Assert(limits.FiveHour?.RemainingPercent==80&&limits.Weekly?.RemainingPercent==20&&limits.AvailableResets==3&&limits.Plan=="pro");
    Assert(limits.FiveHour!.ResetsAt==DateTimeOffset.FromUnixTimeSeconds(1800000100));return Task.CompletedTask;
});
await Test("missing limits are unknown and unrelated durations are not relabeled",()=>
{
    using var json=JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":null,"resetsAt":null},"secondary":{"usedPercent":5,"windowDurationMins":60}},"rateLimitResetCredits":null}""");
    var limits=AccountLimits.Parse(json.RootElement);Assert(limits.FiveHour==null&&limits.Weekly==null&&limits.AvailableResets==null);return Task.CompletedTask;
});
await Test("legacy quota zero and full usage remain distinguishable",()=>
{
    using var json=JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":null},"secondary":{"usedPercent":100,"windowDurationMins":10080,"resetsAt":9223372036854775807}},"rateLimitResetCredits":{"availableCount":0}}""");
    var limits=AccountLimits.Parse(json.RootElement);Assert(limits.FiveHour?.RemainingPercent==100&&limits.Weekly?.RemainingPercent==0&&limits.Weekly.ResetsAt==null&&limits.AvailableResets==0);return Task.CompletedTask;
});
await Test("all five encrypted profiles roundtrip and legacy filenames remain",async ()=>
{
    var root=Temp();var v=new Vault(root);var date=new DateOnly(2026,11,20);
    foreach(var slot in Enum.GetValues<Slot>())v.Save(new(slot,Auth("account"+(int)slot),DateTimeOffset.UtcNow,date));
    Assert(File.Exists(Path.Combine(root,"work.dpapi"))&&File.Exists(Path.Combine(root,"personal.dpapi")));
    foreach(var slot in Enum.GetValues<Slot>())Assert(v.Load(slot)?.RenewalDate==date);
    var active=Path.Combine(root,"auth.json");File.WriteAllText(active,Auth("account0"));
    await new Switcher(v,active,new FakeGateway(),new FakeApp()).SwitchAsync(Slot.Account5);
    Assert(AuthDocument.Parse(File.ReadAllText(active)).Identity.Subject=="account4"&&v.Load(Slot.Work)?.RenewalDate==date);
});
await Test("limits gateway reads isolated account and cleans temporary auth",async ()=>
{
    var root=Temp();var home=Path.Combine(root,"live");Directory.CreateDirectory(home);var auth=Auth("test");var active=Path.Combine(home,"auth.json");File.WriteAllText(active,auth);
    var staging=Path.Combine(root,"staging");var result=await new AccountGateway(Environment.ProcessPath!,staging).ReadLimitsAsync(auth,CancellationToken.None);
    Assert(result.AuthJson==auth&&result.Limits.FiveHour?.RemainingPercent==85&&result.Limits.Weekly?.RemainingPercent==60&&result.Limits.AvailableResets==4);
    Assert(File.ReadAllText(active)==auth&&Directory.GetDirectories(staging).Length==0);
});
await Test("graceful exit terminates a hidden tray app without closing unrelated app",async ()=>
{
    var root=Temp();var ready=Path.Combine(root,"ready.txt");var otherReady=Path.Combine(root,"other.txt");
    Process Start(string path) {var info=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true};info.ArgumentList.Add("--tray-fixture");info.ArgumentList.Add(path);return Process.Start(info)!;}
    using var app=Start(ready);using var other=Start(otherReady);
    try
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while(!File.Exists(ready)||!File.Exists(otherReady))await Task.Delay(50,timeout.Token);
        Assert(!app.HasExited&&app.MainWindowHandle==IntPtr.Zero);
        await GracefulAppExit.RequestAsync(new[]{app},timeout.Token);await app.WaitForExitAsync(timeout.Token);
        Assert(!other.HasExited&&File.Exists(ready+".ended"));
    }
    finally {if(!app.HasExited)app.Kill(true);if(!other.HasExited)other.Kill(true);}
});
await Test("graceful exit respects application refusal",async ()=>
{
    var ready=Path.Combine(Temp(),"ready.txt");var info=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true};
    info.ArgumentList.Add("--tray-fixture");info.ArgumentList.Add(ready);info.ArgumentList.Add("--refuse");using var app=Process.Start(info)!;
    try
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));while(!File.Exists(ready))await Task.Delay(50,timeout.Token);
        await Throws(()=>GracefulAppExit.RequestAsync(new[]{app},timeout.Token));Assert(!app.HasExited&&!File.Exists(ready+".ended"));
    }
    finally {if(!app.HasExited)app.Kill(true);}
});
Console.WriteLine($"{count-failures}/{count} passed");
return failures == 0 ? 0 : 1;

sealed class FakeGateway : IAccountGateway
{
    public int FailCall; public string? ReturnAuth; public Action<int>? OnVerify; int calls;
    public Task<string> VerifyAsync(string authJson,CancellationToken ct)
    { calls++;OnVerify?.Invoke(calls);if(calls==FailCall) throw new IOException("Synthetic verification failure"); return Task.FromResult(ReturnAuth??authJson); }
}
sealed class FakeApp : IAppController
{
    public bool Closed,Launched,CloseFails,LaunchFails;
    public Task CloseAsync(CancellationToken ct) { if(CloseFails) throw new IOException("Synthetic busy"); Closed=true; return Task.CompletedTask; }
    public void EnsureStopped() { if(!Closed) throw new IOException("Still running"); }
    public Task LaunchAsync(CancellationToken ct) { if(LaunchFails) throw new IOException("Synthetic launch failure"); Launched=true; Closed=false; return Task.CompletedTask; }
}
sealed class TrayFixture(string ready,bool refuse) : System.Windows.Forms.Form
{
    protected override void OnShown(EventArgs e) {base.OnShown(e);Hide();File.WriteAllText(ready,"synthetic hidden app");}
    protected override void OnFormClosing(System.Windows.Forms.FormClosingEventArgs e) {e.Cancel=true;Hide();}
    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if(m.Msg==0x11) {m.Result=refuse?IntPtr.Zero:new IntPtr(1);return;}
        if(m.Msg==0x16 && m.WParam!=IntPtr.Zero) {File.WriteAllText(ready+".ended","saved before exit");System.Windows.Forms.Application.ExitThread();return;}
        base.WndProc(ref m);
    }
}

