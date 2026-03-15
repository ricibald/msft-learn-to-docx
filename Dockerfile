# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish MsftLearnToDocx.csproj -c Release -o /app --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS runtime

# Install pandoc and rsvg-convert (for SVG → PNG conversion in DOCX)
RUN apk add --no-cache pandoc rsvg-convert

WORKDIR /app
COPY --from=build /app .
COPY Templates/ ./Templates/

# Output directory (mountable volume)
VOLUME /output

# HTTP response cache (mountable volume for persistence across runs)
VOLUME /cache

ENTRYPOINT ["dotnet", "MsftLearnToDocx.dll"]
