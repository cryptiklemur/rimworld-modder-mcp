# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy solution and project files first for better layer caching
# These files change less frequently than source code
COPY *.sln .
COPY src/RimWorldModderMcp/*.csproj ./src/RimWorldModderMcp/

# Restore dependencies - this layer will be cached if project files don't change
RUN dotnet restore

# Copy source code last - this changes most frequently
COPY src/ ./src/

# Build and publish the application
RUN dotnet publish src/RimWorldModderMcp/RimWorldModderMcp.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app

# Install all system packages in a single layer for better caching
# Combine related package installations to minimize layers
RUN apk add --no-cache \
    icu-libs

# Create user in a single layer
RUN addgroup -g 1001 -S dotnet && \
    adduser -S dotnet -u 1001

# Copy application files before user switch for proper ownership
COPY --from=build /app/publish .
RUN chown -R dotnet:dotnet /app

# Switch to non-root user
USER dotnet

# Set environment variables in a single layer
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Volume declaration
VOLUME ["/rimworld"]

# Runtime configuration
ENTRYPOINT ["dotnet", "RimWorldModderMcp.dll"]
CMD ["--rimworld-path=/rimworld", "--mod-dirs=/rimworld/Mods,/workshop"]
