FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY BakeSmartPatri.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish BakeSmartPatri.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "BakeSmartPatri.dll"]
