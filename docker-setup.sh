#!/bin/sh

# Color codes
BLUE="\033[1;34m"
GREEN="\033[1;32m"
RESET="\033[0m"

status() {
    printf "${BLUE}→ %s...${RESET}\n" "$1"
}

done_status() {
    printf "${GREEN}✓ %s done${RESET}\n" "$1"
}

# Create directories
status "Creating directories"
mkdir -p {environments,log,exports} && done_status "Directories"

# Download files
status "Downloading appsettings.json"
curl -fSL https://raw.githubusercontent.com/melosso/trignis/refs/heads/main/Source/appsettings.json -o appsettings.json && done_status "appsettings.json"

status "Downloading environments/example.json"
curl -fSL https://raw.githubusercontent.com/melosso/trignis/refs/heads/main/Source/environments/example.json -o environments/example.json && done_status "environments/example.json"

status "Downloading docker-compose.yml"
curl -fSL https://raw.githubusercontent.com/melosso/trignis/refs/heads/main/docker-compose.yml -o docker-compose.yml && done_status "docker-compose.yml"

# Prompt at the end
printf "${GREEN}Setup complete. You can now run 'docker compose up -d' if needed.${RESET}\n"
