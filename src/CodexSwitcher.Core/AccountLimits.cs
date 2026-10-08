using System.Text.Json;

namespace CodexSwitcher.Core;

public sealed record QuotaWindow(double RemainingPercent, DateTimeOffset? ResetsAt);
public sealed record ResetCredit(string Id,DateTimeOffset? ExpiresAt,bool HasExpirationInfo,DateTimeOffset? GrantedAt,string? Title);
public sealed record AccountLimits(QuotaWindow? FiveHour, QuotaWindow? Weekly, int? AvailableResets, string? Plan, DateTimeOffset FetchedAt, IReadOnlyList<ResetCredit>? ResetCredits=null)
{
    public static AccountLimits Parse(JsonElement response,string? plan=null)
    {
        JsonElement bucket=default;
        if(response.TryGetProperty("rateLimitsByLimitId",out var map) && map.ValueKind==JsonValueKind.Object && map.TryGetProperty("codex",out var codex)) bucket=codex;
        else if(response.TryGetProperty("rateLimits",out var legacy) && legacy.ValueKind==JsonValueKind.Object &&
            (!legacy.TryGetProperty("limitId",out var id) || id.ValueKind==JsonValueKind.Null || id.GetString()=="codex")) bucket=legacy;
        QuotaWindow? five=null,week=null;
        if(bucket.ValueKind==JsonValueKind.Object)
        {
            if(bucket.TryGetProperty("planType",out var p) && p.ValueKind==JsonValueKind.String) plan=p.GetString();
            foreach(var name in new[]{"primary","secondary"})
            {
                if(!bucket.TryGetProperty(name,out var window) || window.ValueKind!=JsonValueKind.Object ||
                    !window.TryGetProperty("windowDurationMins",out var duration) || duration.ValueKind!=JsonValueKind.Number || !duration.TryGetInt64(out var mins) ||
                    !window.TryGetProperty("usedPercent",out var used) || used.ValueKind!=JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) continue;
                DateTimeOffset? reset=null;
                if(window.TryGetProperty("resetsAt",out var at) && at.ValueKind==JsonValueKind.Number && at.TryGetInt64(out var seconds))
                    try {reset=DateTimeOffset.FromUnixTimeSeconds(seconds);} catch(ArgumentOutOfRangeException) { }
                var quota=new QuotaWindow(Math.Clamp(100-percent,0,100),reset);
                if(mins==300) five=quota; else if(mins==10080) week=quota;
            }
        }
        int? resets=null;IReadOnlyList<ResetCredit>? details=null;
        if(response.TryGetProperty("rateLimitResetCredits",out var credits) && credits.ValueKind==JsonValueKind.Object)
        {
            if(credits.TryGetProperty("availableCount",out var count) && count.ValueKind==JsonValueKind.Number && count.TryGetInt32(out var available) && available>=0) resets=available;
            if(credits.TryGetProperty("credits",out var rows) && rows.ValueKind==JsonValueKind.Array)
            {
                var list=new List<ResetCredit>();var ids=new HashSet<string>(StringComparer.Ordinal);
                foreach(var row in rows.EnumerateArray())
                {
                    if(row.ValueKind!=JsonValueKind.Object || !row.TryGetProperty("id",out var id) || id.ValueKind!=JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))continue;
                    if(row.TryGetProperty("status",out var status) && status.ValueKind==JsonValueKind.String && status.GetString()!="available")continue;
                    if(!ids.Add(id.GetString()!))continue;
                    var expires=Timestamp(row,"expiresAt",out var known);
                    var granted=Timestamp(row,"grantedAt",out _);
                    var title=row.TryGetProperty("title",out var t) && t.ValueKind==JsonValueKind.String?t.GetString():null;
                    list.Add(new ResetCredit(id.GetString()!,expires,known,granted,title));
                }
                details=list.OrderBy(c=>c.ExpiresAt??DateTimeOffset.MaxValue).ToArray();
            }
        }
        return new(five,week,resets,plan,DateTimeOffset.UtcNow,details);
    }
    private static DateTimeOffset? Timestamp(JsonElement row,string key,out bool known)
    {
        known=false;if(!row.TryGetProperty(key,out var value))return null;
        if(value.ValueKind==JsonValueKind.Null) {known=true;return null;}
        if(value.ValueKind==JsonValueKind.Number && value.TryGetInt64(out var seconds))
            try {var result=DateTimeOffset.FromUnixTimeSeconds(seconds);known=true;return result;}catch(ArgumentOutOfRangeException) { }
        return null;
    }
}
public sealed record LimitsReadResult(AccountLimits Limits,string AuthJson);
