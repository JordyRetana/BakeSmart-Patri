FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["BakeSmartPatri.csproj", "./"]
RUN dotnet restore "BakeSmartPatri.csproj"

COPY . .
RUN dotnet publish "BakeSmartPatri.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "BakeSmartPatri.dll"]
