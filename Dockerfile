FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT=BankingApp
WORKDIR /src
COPY src/ ./
RUN dotnet publish ${PROJECT}/${PROJECT}.csproj -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG PROJECT=BankingApp
ENV APP_DLL=${PROJECT}.dll ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --from=build /out .
USER app
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
