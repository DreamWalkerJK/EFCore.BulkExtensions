param(
    [string]$FipsRoot = 'D:\Documents\Code\FIPS\FIPS2022',
    [string]$MonicaRoot = 'D:\Documents\Code\FIPS\MoLibrary',
    [string]$OutputPath = (Join-Path $PSScriptRoot 'source-audit.json')
)

$ErrorActionPreference = 'Stop'

function Get-SourceEvidence {
    param([string]$Root, [string[]]$RelativePaths)
    @($RelativePaths | ForEach-Object {
        $relativePath = $_
        $absolutePath = Join-Path $Root $relativePath
        if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
            throw "源码审计文件不存在：$absolutePath"
        }
        [ordered]@{
            Path = $relativePath.Replace('\', '/')
            Sha256 = (Get-FileHash -LiteralPath $absolutePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}

function Get-RepositoryState {
    param([string]$Root)
    $head = & git -C $Root rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw "无法读取源码版本：$Root" }
    $status = @(& git -C $Root status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw "无法读取源码工作区状态：$Root" }
    [ordered]@{
        Root = $Root
        Head = $head.Trim()
        WorkingTreeClean = $status.Count -eq 0
        ChangedPaths = $status
    }
}

$fipsFiles = @(
    'AGENTS.md',
    'common.xml',
    'src/Shared/Platform.Infrastructure/Platform.Infrastructure.csproj',
    'src/Shared/Platform.Infrastructure/BuilderExtensions/DbContextShardingTableExtensions.cs',
    'src/Shared/Platform.BuildingBlocks/Persistence/FipsDbContext.cs',
    'src/Shared/Platform.BuildingBlocks/Persistence/FipsHistoryDbContext.cs',
    'src/Shared/Platform.BuildingBlocks/Persistence/FipsFullAuditedEntity.cs',
    'src/Shared/Platform.BuildingBlocks/DataSync/FipsPersistencePolicy.cs',
    'src/Services/Flight/FlightService.Domain/Repository/FlightDbContext.cs',
    'src/Services/Flight/FlightService.Domain/Entities/Flight.cs',
    'src/Services/Flight/FlightService.Domain/Entities/FlightPlan.cs',
    'src/Services/Flight/FlightService.Domain/Entities/FlightOtherInfo.cs',
    'src/Services/Flight/FlightService.Domain/Entities/FlightDepInfo.cs',
    'src/Services/Flight/FlightService.Domain/Entities/FlightArrInfo.cs',
    'src/Services/Flight/FlightService.Domain/Entities/FlightRteInfo.cs',
    'src/Services/Flight/FlightService.Domain/Entities/PreFlightPlan.cs',
    'src/Services/FlightRoute/FlightRouteService.Domain/Entities/Airway.cs',
    'src/Services/FlightRoute/FlightRouteService.Domain/Entities/AirwayPoint.cs',
    'src/Services/FlightRoute/FlightRouteService.Domain/Entities/AirwaySegmentPoint.cs',
    'src/Services/User/UserService.Domain/Entities/User.cs',
    'src/Services/Message/MessageService.Domain/Entities/InMessage.cs',
    'src/Services/Message/MessageService.Domain/Entities/MessageDetail.cs',
    'src/Services/Message/MessageService.Domain/Entities/PlanNoticeMsg.cs',
    'src/Services/Message/MessageService.Domain/Entities/PlanMorrowMsg.cs',
    'src/Services/MilitaryFlight/MilitaryFlightService.Domain/EFCoreEncryption/ModelBuilderExtensions.cs',
    'src/Services/DataExchange/DataExchangeService.Domain/Entities/MessageExchange/MessageOutboundBatch.cs',
    'src/Services/DataExchange/DataExchangeService.Domain/Entities/MessageExchange/MessageInboundReceipt.cs',
    'src/Services/DataExchange/DataExchangeService.Domain/Entities/MessageExchange/MessageInboundContent.cs',
    'src/Services/DataExchange/DataExchangeService.Domain/Entities/MessageExchange/MessageCircuitState.cs',
    'skills/fips-application-spec/SKILL.md',
    'skills/fips-application-spec/references/persistence.md'
)

$monicaFiles = @(
    'AGENTS.md',
    'Monica.Repository/Monica.Repository.csproj',
    'Monica.Repository/Persistence/Services/RepositoryDbContext.cs',
    'Monica.Repository/Persistence/Services/PersistencePolicies.cs',
    'Monica.Repository/Persistence/Services/Support/RepositoryOperationInterceptor.cs',
    'Monica.Repository/Persistence/Services/Support/ObjectToJsonConverter.cs',
    'Monica.Repository/Persistence/Services/Support/ExtraPropertiesValueConverter.cs',
    'Monica.Repository/Entity/Services/AuditPropertySetter.cs',
    'Monica.Repository/Entity/Extensions/EntityTypeBuilderExtensions.cs',
    'Monica.Repository/UnitOfWork/Services/UnitOfWorkManager.cs',
    'skills/monica-application/SKILL.md',
    'skills/monica-infra-persistence/SKILL.md',
    'skills/monica-infra-persistence/references/repositories.md',
    'skills/monica-infra-persistence/references/transactions.md',
    'skills/monica-unit-testing/SKILL.md'
)

$fipsState = Get-RepositoryState -Root $FipsRoot
$monicaState = Get-RepositoryState -Root $MonicaRoot
$fipsState.Evidence = Get-SourceEvidence -Root $FipsRoot -RelativePaths $fipsFiles
$monicaState.Evidence = Get-SourceEvidence -Root $MonicaRoot -RelativePaths $monicaFiles

$audit = [ordered]@{
    SchemaVersion = 1
    CollectedUtc = [DateTime]::UtcNow.ToString('O')
    Scope = '只读源码审计；本文件不代表运行测试、生产接入或完整 FIPS 功能验证。'
    Fips = $fipsState
    Monica = $monicaState
}
$audit | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host ("已记录 FIPS {0}、Monica {1}，共 {2} 个源码证据文件。" -f $fipsState.Head, $monicaState.Head, ($fipsFiles.Count + $monicaFiles.Count))
