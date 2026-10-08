using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexSwitcher.Core;

public sealed class Vault(string root)
{
    public string Root { get; } = root;
    private string ProfilePath(Slot slot) => Path.Combine(Root, slot switch
    { Slot.Work => "work.dpapi", Slot.Personal => "personal.dpapi", _ when (int)slot>=2&&(int)slot<int.MaxValue => "account"+((int)slot+1).ToString(System.Globalization.CultureInfo.InvariantCulture)+".dpapi", _ => throw new ArgumentOutOfRangeException(nameof(slot)) });
    private string SlotsPath=>Path.Combine(Root,"accounts.json");
    public IReadOnlyList<Slot> Slots()
    {
        SecureFiles.RejectLinks(Root);SecureFiles.RejectLinks(SlotsPath);
        var slots=File.Exists(SlotsPath)?new SortedSet<int>():new SortedSet<int> {0,1};
        if(File.Exists(SlotsPath))
        {
            try
            {
                foreach(var id in JsonSerializer.Deserialize<int[]>(File.ReadAllText(SlotsPath))??throw new JsonException())
                {if(id<0||id==int.MaxValue)throw new JsonException();slots.Add(id);}
            }
            catch(JsonException) {throw new UserFault("Список аккаунтов повреждён. Сохранённые входы остаются в хранилище.");}
        }
        if(File.Exists(ProfilePath(Slot.Work)))slots.Add(0);
        if(File.Exists(ProfilePath(Slot.Personal)))slots.Add(1);
        if(Directory.Exists(Root))foreach(var path in Directory.EnumerateFiles(Root,"account*.dpapi"))
        {
            var name=Path.GetFileNameWithoutExtension(path);
            if(int.TryParse(name.AsSpan(7),out var number)&&number>=3)slots.Add(number-1);
        }
        return slots.Select(id=>(Slot)id).ToArray();
    }
    public Slot AddSlot()
    {
        var slots=Slots().Select(slot=>(int)slot).ToList();
        var last=slots.Count==0?-1:slots[^1];
        if(last>=int.MaxValue-1)throw new UserFault("Не удалось добавить новый номер аккаунта.");
        var next=last+1;slots.Add(next);SecureFiles.PrivateDirectory(Root);
        SecureFiles.AtomicText(SlotsPath,JsonSerializer.Serialize(slots));return (Slot)next;
    }
    public void RemoveEmptySlot(Slot slot)
    {
        var path=ProfilePath(slot);SecureFiles.RejectLinks(path);
        if(File.Exists(path))throw new UserFault("Эта карточка содержит сохранённый вход. Удалять можно только пустые карточки.");
        if(HasRecovery)throw new UserFault("Сначала завершите восстановление предыдущего переключения.");
        var slots=Slots().Where(candidate=>candidate!=slot).Select(candidate=>(int)candidate).ToArray();
        SecureFiles.PrivateDirectory(Root);SecureFiles.AtomicText(SlotsPath,JsonSerializer.Serialize(slots));
    }
    private string JournalPath => Path.Combine(Root,"recovery.dpapi");
    public bool HasRecovery => File.Exists(JournalPath);
    public Profile? Load(Slot slot)
    {
        var path = ProfilePath(slot);
        if (!File.Exists(path)) return null;
        var profile = Read<Profile>(path);
        if (profile.Slot != slot) throw new UserFault("Профиль не соответствует выбранному аккаунту.");
        AuthDocument.Parse(profile.AuthJson);
        return profile;
    }
    public void Save(Profile profile)
    {
        AuthDocument.Parse(profile.AuthJson);
        Write(ProfilePath(profile.Slot),profile);
    }
    public void BeginRecovery(RecoveryRecord record)
    {
        if (HasRecovery) throw new UserFault("Сначала завершите восстановление предыдущего переключения.");
        Write(JournalPath, record);
    }
    public RecoveryRecord ReadRecovery() => Read<RecoveryRecord>(JournalPath);
    public void ClearRecovery() { SecureFiles.RejectLinks(JournalPath); File.Delete(JournalPath); }
    private void Write<T>(string path, T value)
    {
        SecureFiles.PrivateDirectory(Root);
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try { SecureFiles.AtomicWrite(path, Dpapi.Protect(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private T Read<T>(string path)
    {
        SecureFiles.RejectLinks(path);
        var bytes = Dpapi.Unprotect(File.ReadAllBytes(path));
        try { return JsonSerializer.Deserialize<T>(bytes) ?? throw new UserFault("Защищённый файл повреждён."); }
        catch (JsonException) { throw new UserFault("Защищённый файл повреждён. Повторите вход."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
public sealed record RecoveryRecord(string AuthPath, string? OriginalAuth, string TargetKey, Slot TargetSlot);
