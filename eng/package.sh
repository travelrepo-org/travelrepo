#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
dotnet pack TravelRepo.sln -c Release -o artifacts/packages
