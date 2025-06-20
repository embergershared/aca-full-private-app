#!/bin/bash
az cli --version
touch /2025-06-16-custom-script-is-here.txt

# Install Docker for Ubuntu with convenient script
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh ./get-docker.sh --dry-run