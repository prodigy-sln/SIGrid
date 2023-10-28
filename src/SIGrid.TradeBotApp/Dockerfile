#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:7.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["src/SIGrid.TradeBotApp/SIGrid.TradeBotApp.csproj", "src/SIGrid.TradeBotApp/"]
COPY ["src/SIGrid.Exchange.Shared/SIGrid.Exchange.Shared.csproj", "src/SIGrid.Exchange.Shared/"]
COPY ["src/SIGrid.TradeBot/SIGrid.TradeBot.csproj", "src/SIGrid.TradeBot/"]
COPY ["libs/OKX.Net/OKX.Net/OKX.Net.csproj", "libs/OKX.Net/OKX.Net/"]
RUN dotnet restore "src/SIGrid.TradeBotApp/SIGrid.TradeBotApp.csproj"
COPY . .
WORKDIR "/src/src/SIGrid.TradeBotApp"
RUN dotnet build "SIGrid.TradeBotApp.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SIGrid.TradeBotApp.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SIGrid.TradeBotApp.dll"]