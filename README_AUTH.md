# Wubble: авторизация

Все маршруты без версионирования: `/api/auth/...`, `/api/health`.

| Метод | Маршрут | JSON | Успех |
|---|---|---|---|
| POST | /api/auth/register | email, username, password | 201, пользователь |
| POST | /api/auth/login | email, password | 204, две cookie |
| POST | /api/auth/refresh | без тела | 204, новые cookie |
| POST | /api/auth/logout | без тела | 204, cookie удалены |
| GET | /api/auth/me | Authorization: Bearer JWT | 200, пользователь |
| POST | /api/auth/verify-email | userId, token | 204 |
| POST | /api/auth/resend-verification | email | 202 |

Регистрация не выполняет вход. До подтверждения email правильный пароль даёт 403
с code=email_not_confirmed. Username: 3–32 символа, латиница, цифры, _, -, .;
пароль: 8–128 символов. Пять неверных попыток блокируют вход на пять минут.

Access: JWT HS256, 15 минут, cookie `wubble_access`, HttpOnly=false, Secure,
SameSite=Strict, Path=/. API читает JWT только из Authorization.
Refresh: случайные 32 байта, 7 дней с момента каждой ротации, cookie
`wubble_refresh`, HttpOnly=true, Secure, SameSite=Strict, Path=/api/auth.
В БД хранится только SHA-256 refresh. При ротации заменяется и access, и refresh.

Logout отзывает текущую сессию (всю цепочку refresh), другие входы сохраняются.
JWT содержит sid; каждый защищённый запрос дополнительно проверяет активность
сессии в БД. Поэтому после завершения logout старый JWT не проходит проверку.
Уже выполняющийся запрос logout не отменяет. Старые JWT без sid потребуют нового входа.
Rotation/logout/replay сериализованы транзакционной advisory lock PostgreSQL.
При повторе старого refresh после 30 секунд отзывается вся цепочка; в первые
30 секунд возвращается 409 без выдачи токенов. Вкладки должны согласовывать refresh:
409 сам по себе не гарантирует, что ответ победившего запроса уже обновил cookie.

## Локальный запуск

Нужны .NET SDK 10, Docker, dotnet-ef версии 10.
Из корня:

```sh
docker compose up -d
dotnet dev-certs https --trust
dotnet user-secrets set 'Jwt:SigningKey' '<случайный секрет минимум 32 байта>' --project Wubble.Api
dotnet tool restore
dotnet restore Wubble.Api
dotnet ef database update --project Wubble.Api
dotnet run --project Wubble.Api --launch-profile https
```

API: https://localhost:7051, Scalar: https://localhost:7051/scalar,
почтовая ловушка Mailpit: http://localhost:8025.
Frontend запускайте на https://localhost:5173 с доверенным сертификатом.
В development намеренно нет отправки реальных писем: они попадают в Mailpit.
Миграция UniqueEmail добавляет уникальный EmailIndex; если в существующей БД
уже есть дубликаты NormalizedEmail, сначала устраните их вручную.

## Frontend

Для login/refresh/logout/register/resend/verify используйте credentials: 'include'.
После login читайте wubble_access через document.cookie и передавайте JWT:

```js
const token = document.cookie.split('; ').find(x => x.startsWith('wubble_access='))?.split('=')[1];
const response = await fetch(`${api}/api/auth/me`, {
  headers: { Authorization: `Bearer ${decodeURIComponent(token ?? '')}` },
  credentials: 'include'
});
```

На 401 выполните один общий для ожидающих запросов refresh, затем повторите запрос
с новым access. Не запускайте бесконечные повторы. Для нескольких вкладок используйте
Web Locks/BroadcastChannel. На 409 дождитесь завершения refresh другой вкладки;
если обновление не удалось, предложите повторный вход.
Ссылка письма ведёт на `${Auth:FrontendBaseUrl}/verify-email?userId=...&token=...`.
Эта страница должна извлечь параметры URL и отправить POST /api/auth/verify-email.

## Развёртывание и почта

Задайте через окружение/секреты:

- ConnectionStrings__Default — PostgreSQL.
- Jwt__SigningKey — случайный секрет, минимум 32 байта.
- Auth__FrontendBaseUrl — HTTPS URL frontend.
- Cors__AllowedOrigins__0 — точный origin frontend, без завершающего /.
- Email__Host, Email__Port, Email__Security (StartTls или SslOnConnect),
  Email__Username, Email__Password — параметры реального SMTP-провайдера.
- Email__FromAddress = wubble@alisaboules.ru (уже по умолчанию).

SMTP-сервер нельзя определить только по адресу отправителя. Настройте ящик и
SPF/DKIM/DMARC у почтового провайдера. Реальная доставка в этой среде не проверялась.
Для frontend/API на разных поддоменах одного сайта задайте
Auth__AccessTokenCookieDomain общим родительским доменом; refresh останется host-only.
Предпочтительно публиковать API под /api того же origin, что и frontend.
Несвязанные домены не подходят для текущей схемы: frontend не сможет читать cookie API,
а SameSite=Strict блокирует cross-site cookie. Изменение этой архитектуры требует
отдельного решения. JS-доступ к access означает, что защита frontend от XSS обязательна.
За reverse proxy настройте доверенные ForwardedHeaders до HTTPS redirection.

## Проверка

Добавлен консольный интеграционный набор Wubble.AuthChecks без отдельного тестового
фреймворка: ротация, replay, отзыв сессии, конкурентные refresh/logout и refresh/refresh.
Он требует отдельную PostgreSQL БД и применяет миграции, затем удаляет своего пользователя.

```sh
WUBBLE_TEST_DB='Host=localhost;Database=wubble_auth_tests;Username=wubble;Password=wubble' dotnet run --project Wubble.AuthChecks
```

Дополнительно проверьте HTTP-сценарий через Scalar/frontend: регистрация → письмо в
Mailpit → подтверждение → login → me → refresh → logout → старый JWT даёт 401;
чужой Origin на login/refresh/logout даёт 403, без Bearer на me — 401.

При добавлении мониторинга выполнены сборка решения на .NET 10 и интеграционные
проверки Wubble.AuthChecks на PostgreSQL 17. Подробности — в README_MONITORING.md.
