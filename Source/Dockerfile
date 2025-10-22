FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj and restore as distinct layers
COPY ./*.sln ./
COPY Source/Trignis.csproj ./Source/
RUN dotnet restore ./Source/Trignis.csproj

# Copy everything and build
COPY . .
WORKDIR /src/Source
RUN dotnet publish Trignis.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80

ENTRYPOINT ["dotnet", "Trignis.dll"]
