FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Crawler.Domain/Crawler.Domain.csproj src/Crawler.Domain/
COPY src/Crawler.Contracts/Crawler.Contracts.csproj src/Crawler.Contracts/
COPY src/Crawler.Infrastructure/Crawler.Infrastructure.csproj src/Crawler.Infrastructure/
COPY src/Crawler.Api/Crawler.Api.csproj src/Crawler.Api/

RUN dotnet restore src/Crawler.Api/Crawler.Api.csproj

COPY src/ src/
RUN dotnet publish src/Crawler.Api/Crawler.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

USER $APP_UID
ENTRYPOINT ["dotnet", "Crawler.Api.dll"]

