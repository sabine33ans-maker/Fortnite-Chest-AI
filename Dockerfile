# Build the real LED-N ChestCounter replay parser.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS analyzer-build
WORKDIR /src
COPY chestcounter-src ./chestcounter-src
COPY analyzer ./analyzer
RUN dotnet publish analyzer/ChestReplayCli.csproj -c Release -o /out

# Run the Python web app and the .NET 10 replay parser in one container.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip tzdata \
    && rm -rf /var/lib/apt/lists/*
COPY requirements.txt .
RUN python3 -m pip install --break-system-packages --no-cache-dir -r requirements.txt
COPY . .
COPY --from=analyzer-build /out ./analyzer
ENV DATA_DIR=/data
ENV CHEST_ANALYZER_COMMAND="dotnet /app/analyzer/ChestReplayCli.dll"
EXPOSE 10000
CMD ["sh", "-c", "python3 -m uvicorn main:app --host 0.0.0.0 --port ${PORT:-10000}"]
