using System.Net;

namespace Wubble.Api.Email;

public static class EmailTemplates
{
    public static OutgoingEmail Verification(string to, string username, string link)
    {
        var safeName = WebUtility.HtmlEncode(username);
        var safeLink = WebUtility.HtmlEncode(link);

        var html = $"""
            <p>Здравствуйте, {safeName}!</p>
            <p>Чтобы завершить регистрацию в Wubble, подтвердите адрес электронной почты:</p>
            <p><a href="{safeLink}">Подтвердить email</a></p>
            <p>Ссылка действует 24 часа. Если вы не регистрировались в Wubble, просто проигнорируйте это письмо.</p>
            """;

        var text = $"""
            Здравствуйте, {username}!

            Чтобы завершить регистрацию в Wubble, подтвердите адрес электронной почты:
            {link}

            Ссылка действует 24 часа. Если вы не регистрировались в Wubble, просто проигнорируйте это письмо.
            """;

        return new OutgoingEmail(to, "Подтвердите email для Wubble", html, text);
    }
}
