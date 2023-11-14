#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:7.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["src/SIGrid.App.GridBot/SIGrid.App.GridBot.csproj", "src/SIGrid.App.GridBot/"]
COPY ["libs/OKX.Net/OKX.Net/OKX.Net.csproj", "libs/OKX.Net/OKX.Net/"]
RUN dotnet restore "src/SIGrid.App.GridBot/SIGrid.App.GridBot.csproj"
COPY . .
WORKDIR "/src/src/SIGrid.App.GridBot"
RUN dotnet build "SIGrid.App.GridBot.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SIGrid.App.GridBot.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SIGrid.App.GridBot.dll"]
