# Use the official .NET SDK image to build the app
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

EXPOSE 5000-5999/tcp

WORKDIR /app

# Copy project files and restore dependencies
COPY *.csproj ./

# Copy nuget.config before restore
COPY nuget.config /root/.nuget/NuGet/NuGet.Config

RUN dotnet restore

# Copy the rest of the source and build the app
COPY . ./
RUN dotnet publish -c Release -o /out

# Runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0

WORKDIR /app

# Copy the published output from build stage
COPY --from=build /out .

# After publishing your .NET app
COPY ./Artifacts/ /app/config/
COPY ./Artifacts/DataPlatform.config.full.json /app/config/config.full.json
COPY ./Artifacts/DataPlatform.config.full.json /app/config/config.full.json

# Optional: set env variable
ENV CONFIG_PATH=/app/config/config.full.json

# Set a default command that does nothing (overridden when you manually exec)
CMD ["tail", "-f", "/dev/null"]
