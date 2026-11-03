#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Application/ConfigServer.Application.Api/ConfigServer.Application.Api.csproj", "Application/ConfigServer.Application.Api/"]
RUN dotnet restore "Application/ConfigServer.Application.Api/ConfigServer.Application.Api.csproj"
COPY . .
WORKDIR "/src/Application/ConfigServer.Application.Api"
RUN dotnet build "ConfigServer.Application.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ConfigServer.Application.Api.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ConfigServer.Application.Api.dll"]