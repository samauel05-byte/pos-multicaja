#!/usr/bin/env bash
# Ejecuta TODAS las pruebas de integración contra un PostgreSQL real (cada clase crea y elimina su propia base).
# Variables: POS_TEST_ADMIN_CONN (cadena con permiso CREATE DATABASE), POS_PG_BIN (carpeta de pg_dump/pg_restore).
set -euo pipefail
cd "$(dirname "$0")/../server"
source ../scripts/dev-env.sh
dotnet test tests/Pos.Tests "$@"
