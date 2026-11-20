#!/usr/bin/env bash
set -euo pipefail
cd /opt/inventory-manager
test -f .env || { echo "Missing .env"; exit 1; }
mkdir -p /var/backups/inventory-manager
chmod 700 /var/backups/inventory-manager
stamp=$(date -u +%Y%m%dT%H%M%SZ)
file="/var/backups/inventory-manager/inventory-${stamp}.sql.gz"
docker compose --env-file .env -f deploy/compose.yml exec -T db pg_dump -U inventory -d inventory --no-owner --no-acl | gzip -9 > "$file"
test -s "$file" || { rm -f "$file"; echo "Backup failed"; exit 1; }
chmod 600 "$file"
find /var/backups/inventory-manager -type f -name 'inventory-*.sql.gz' -mtime +14 -delete
echo "Backup saved: $file"
