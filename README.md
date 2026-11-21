# Wubble backend

ASP.NET Core 10 + Identity + EF Core + PostgreSQL.

Backend поддерживает авторизацию, HTTP-мониторинг, расписание, историю,
статистику, инциденты и email-уведомления владельцу. Маршруты: `/api/auth/...`
и `/api/monitors/...`, без версионирования.

- [Запуск и HTTP-мониторинг](README_MONITORING.md)
- [Авторизация и настройки почты](README_AUTH.md)
- [Секреты: локальный запуск и Render](README_SECRETS.md)
- [Примеры запросов](Wubble.Api/Monitoring.http)
- [OpenAPI, сгенерированный из текущего кода](openapi.json)
- [Результаты проверки](VALIDATION.md)

После обновления существующего проекта примените новую миграцию:

```sh
dotnet tool restore
dotnet ef database update --project Wubble.Api
dotnet run --project Wubble.Api --launch-profile https
```

Перед первым запуском настройте User Secrets по [README_SECRETS.md](README_SECRETS.md).
PostgreSQL и Mailpit описаны в README_MONITORING.md.
