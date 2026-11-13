# Wubble backend

ASP.NET Core 10 + Identity + EF Core + PostgreSQL.

Backend поддерживает авторизацию, HTTP-мониторинг, расписание, историю,
статистику, инциденты и email-уведомления владельцу. Маршруты: `/api/auth/...`
и `/api/monitors/...`, без версионирования.

- [Запуск и HTTP-мониторинг](README_MONITORING.md)
- [Авторизация и настройки почты](README_AUTH.md)
- [Примеры запросов](Wubble.Api/Monitoring.http)
- [OpenAPI, сгенерированный из текущего кода](openapi.json)
- [Результаты проверки](VALIDATION.md)

После обновления существующего проекта примените новую миграцию:

```sh
dotnet tool restore
dotnet ef database update --project Wubble.Api
dotnet run --project Wubble.Api --launch-profile https
```

Для первого запуска PostgreSQL, Mailpit и JWT-секрета следуйте README_MONITORING.md.
