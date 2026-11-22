# Public development preview

Public URL: https://atlas.lalapilot.com
Runtime: Mac8, local port 15809; development remains on Mac16.
The existing Cloudflare tunnel routes the public hostname to VPS loopback 25809, then an SSH reverse tunnel to Mac8 loopback 15809. The origin certificate remains validated using the deployment CA. Project authentication remains enabled.

Mac8 stores the persistent public origin in ~/atlas-deploy/product/runtime-config.json, so later deployments keep the public login/callback origin. Credentials stay in private server configuration and are not published here. Source publication to GitHub is a separate, later action.
