    [CmdletBinding(PositionalBinding = $false)]
    Param(
        [string] $configuration,
        [string] $buildSourcesDirectory,
        [string] $customHelixTargetQueue,
        [switch] $test
    )

    if (-not $test)
    {
        Write-Output "No '-test' switch. Skip both helix and non helix tests"
        return
    }

    workflow runHelixAndNonHelixInParallel
    {
        Param(
            [string] $configuration,
            [string] $buildSourcesDirectory,
            [string] $customHelixTargetQueue,
            [string] $engfolderPath
        )

        parallel {
            RunTestsCannotRunOnHelix -configuration $configuration -engfolderPath $engfolderPath
            RunTestsOnHelix -configuration $configuration -buildSourcesDirectory $buildSourcesDirectory -customHelixTargetQueue $customHelixTargetQueue -engfolderPath $engfolderPath
        }
    }

    runHelixAndNonHelixInParallel -configuration $configuration -buildSourcesDirectory $buildSourcesDirectory -customHelixTargetQueue $customHelixTargetQueue -engfolderPath $PSScriptRoot

    function RunTestsCannotRunOnHelix(
        [string] $configuration,
        [string] $engfolderPath
    )
    {
        Write-Host "Running tests that can't run on Helix..."
        $runTestsCannotRunOnHelixArgs = ("-configuration", $configuration, "-ci")
        Invoke-Expression "&'$engfolderPath\runTestsCannotRunOnHelix.ps1' $runTestsCannotRunOnHelixArgs"
        Write-Host "Done running tests that can't run on Helix..."
    }

    function RunTestsOnHelix(
        [string] $configuration,
        [string] $buildSourcesDirectory,
        [string] $customHelixTargetQueue,
        [string] $engfolderPath
    )
    {
        Write-Host "Running tests in Helix..."

        $runTestsOnHelixArgs = ("-configuration", $configuration,
        "-prepareMachine",
        "-ci",
        "-restore",
        "-test",
        "-projects", "$buildSourcesDirectory/src/Tests/UnitTests.proj",
        "/bl:$buildSourcesDirectory\artifacts\log\$configuration\TestInHelix.binlog",
        "/p:_CustomHelixTargetQueue=$customHelixTargetQueue")

        Invoke-Expression "&'$engfolderPath\common\build.ps1' $runTestsOnHelixArgs"

        Write-Host "Done running tests on Helix..."
    }