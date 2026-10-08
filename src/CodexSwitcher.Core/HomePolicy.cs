using System.Text.RegularExpressions;

namespace CodexSwitcher.Core;

public static class HomePolicy
{
    public static void Check(string home,Identity? target=null)
    {
        SecureFiles.RejectLinks(home);
        foreach(var key in new[] {"OPENAI_API_KEY","CODEX_API_KEY","CODEX_ACCESS_TOKEN","ACCESS_TOKEN","CHATGPT_BASE_URL","CODEX_CHATGPT_BASE_URL"})
            if(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                throw new UserFault("Вход задан переменной среды. Переключатель поддерживает обычный файловый вход ChatGPT.");
        var config=Path.Combine(home,"config.toml");
        if(!File.Exists(config)) return;
        SecureFiles.RejectLinks(config);
        var insideTable=false;
        foreach(var line in File.ReadLines(config))
        {
            var text=line.Trim(); if(text.StartsWith('#') || text.Length==0) continue;
            if(text.StartsWith('[')) {insideTable=true;continue;}
            // TOML basic quoted keys decode escapes. This deliberately limited reader must
            // reject those spellings rather than mistaking a protected key for an unrelated one.
            var separator=text.IndexOf('=');
            var spelling=separator>=0?text[..separator]:text;
            if(spelling.Contains('"')&&spelling.Contains('\\'))
                throw new UserFault("Экранированные имена настроек требуют отдельной проверки. Переключение остановлено.");
            var match=Regex.Match(text,"^\\s*[\"']?(cli_auth_credentials_store|forced_login_method|forced_chatgpt_workspace_id|chatgpt_base_url)[\"']?\\s*=\\s*([\"'])([^\"']*)\\2\\s*(?:#.*)?$");
            if(!match.Success)
            {
                var equals=text.IndexOf('=');
                var keyPart=equals>=0?text[..equals]:text;
                if(Regex.IsMatch(keyPart,"cli_auth_credentials_store|forced_login_method|forced_chatgpt_workspace_id|chatgpt_base_url"))
                    throw new UserFault("Нестандартный формат настроек входа. Проверьте config.toml перед переключением.");
                continue;
            }
            if(insideTable) throw new UserFault("Настройки входа внутри профиля требуют отдельной проверки. Переключение остановлено.");
            var key=match.Groups[1].Value; var value=match.Groups[3].Value;
            if(key=="cli_auth_credentials_store" && value!="file") throw new UserFault("Codex использует системное хранилище входа. Эта версия поддерживает только auth.json; настройки не изменены.");
            if(key=="forced_login_method" && value!="chatgpt") throw new UserFault("Настройки Codex запрещают вход через ChatGPT.");
            if(key=="forced_chatgpt_workspace_id" && target!=null && value!=target.AccountId) throw new UserFault("Настройки Codex разрешают другой рабочий аккаунт. Переключение остановлено.");
            if(key=="chatgpt_base_url" && value.TrimEnd('/')!="https://chatgpt.com") throw new UserFault("Нестандартный сервер входа требует отдельной проверки. Переключение остановлено.");
        }
    }
}
