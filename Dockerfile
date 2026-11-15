
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY ["Wubble.Api/Wubble.Api.csproj", "Wubble.Api/"]

RUN dotnet restore "Wubble.Api/Wubble.Api.csproj"

COPY . .

WORKDIR /src/Wubble.Api

RUN dotnet publish "Wubble.Api.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:10000

EXPOSE 10000

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Wubble.Api.dll"]
