# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ClinicalTrialPreScreening.Api.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Trust the corporate TLS-inspecting proxy (Netskope) root/intermediate CA so
# outbound HTTPS calls (e.g. to api.anthropic.com) don't fail with
# "UntrustedRoot" when run on a network that intercepts TLS traffic.
COPY certs/netskope-corporate-ca.crt /usr/local/share/ca-certificates/netskope-corporate-ca.crt
RUN update-ca-certificates

COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ClinicalTrialPreScreening.Api.dll"]
