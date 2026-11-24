---
name: configuration-and-security
description: NMS configuration convention, credentials, and publish exclusion.
---

# Configuration and security

1. Honor ignored `appsettings.Development.json` / `appsettings.Production.json` plus tracked examples.
2. Never commit or print secrets.
3. Keep Development out of publish output.
4. Do not introduce User Secrets or env-var credential redesign unless explicitly requested.
5. Watch for NETSDK1152 / conflicting published appsettings across project references.
