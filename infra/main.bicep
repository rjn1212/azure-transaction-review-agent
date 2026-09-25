// Deploys the whole dev environment on free tiers into an existing resource group.
//   az deployment group create -g rg-txnagent-dev -f infra/main.bicep \
//     -p namePrefix=txnagent-rn sqlAdminPassword='<strong pwd>' openAiApiKey='<key>'
// Tip: build everything by hand in the portal first (to learn it), then use this to recreate it.

@description('Short, globally-unique-ish prefix, e.g. txnagent-rn')
param namePrefix string
param location string = resourceGroup().location
@description('Static Web Apps is only offered in a few regions')
param swaLocation string = 'centralus'
param sqlAdminLogin string = 'txnadmin'
@secure()
param sqlAdminPassword string
@secure()
param openAiApiKey string
param openAiModel string = 'gpt-6-luna'
@description('Optional: your public IP so you can query the database from your Mac (curl -s ifconfig.me)')
param clientIp string = ''

// ---------- Monitoring (stay under the free ingestion allowance) ----------
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${namePrefix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.1') }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${namePrefix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------- Azure SQL: free offer (auto-pauses instead of billing when the monthly allowance is used) ----------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-${namePrefix}'
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource allowClient 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (!empty(clientIp)) {
  parent: sqlServer
  name: 'AllowDevClient'
  properties: { startIpAddress: clientIp, endIpAddress: clientIp }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'TxnAgent'
  location: location
  sku: { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 2 }
  properties: {
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368 // 32 GB
  }
}

// ---------- API: App Service F1 (Linux) ----------
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${namePrefix}'
  location: location
  sku: { name: 'F1', tier: 'Free' }
  kind: 'linux'
  properties: { reserved: true }
}

resource swa 'Microsoft.Web/staticSites@2023-12-01' = {
  name: 'swa-${namePrefix}'
  location: swaLocation
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: 'api-${namePrefix}'
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: false // not available on F1
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'OpenAI__ApiKey', value: openAiApiKey }
        { name: 'OpenAI__Model', value: openAiModel }
        { name: 'OpenAI__MaxRunsPerDay', value: '200' }
        { name: 'RateLimit__AgentPerMinute', value: '10' }
        { name: 'Cors__AllowedOrigins', value: 'https://${swa.properties.defaultHostname},http://localhost:4200' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'ApplicationInsightsAgent_EXTENSION_VERSION', value: '~3' }
      ]
      connectionStrings: [
        {
          name: 'Sql'
          type: 'SQLAzure'
          connectionString: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDb.name};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;Connection Timeout=60'
        }
      ]
    }
  }
}

output apiUrl string = 'https://${api.properties.defaultHostName}'
output webUrl string = 'https://${swa.properties.defaultHostname}'
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
