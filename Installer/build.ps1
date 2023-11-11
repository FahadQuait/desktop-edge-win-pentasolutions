function verifyFile($path) {
    if (Test-Path -Path "$path") {
        "OK: $path exists!"
    } else {
        throw [System.IO.FileNotFoundException] "$path not found"
    }
}

echo "========================== build.ps1 begins =========================="
nuget restore .\ZitiDesktopEdge.sln
$invocation = (Get-Variable MyInvocation).Value
$scriptPath = Split-Path $invocation.MyCommand.Path
$buildPath = "${scriptPath}\build"

echo "Cleaning previous build folder if it exists"
rm "${buildPath}" -r -fo -ErrorAction Ignore
mkdir "${buildPath}" -ErrorAction Ignore > $null

$zet_binary="${buildPath}"
if($null -eq $env:ZITI_EDGE_TUNNEL_BUILD) {
    echo "========================== fetching ziti-edge-tunnel =========================="
    if($null -eq $env:ZITI_EDGE_TUNNEL_VERSION) {
        $zet_dl="https://github.com/openziti/ziti-tunnel-sdk-c/releases/latest/download/ziti-edge-tunnel-Windows_x86_64.zip"
    } else {
        $zet_dl="https://github.com/openziti/ziti-tunnel-sdk-c/releases/download/${env:ZITI_EDGE_TUNNEL_VERSION}/ziti-edge-tunnel-Windows_x86_64.zip"
    }
    echo "Beginning to download ziti-edge-tunnel from ${zet_dl}"
    echo ""
    $ProgressPreference = 'SilentlyContinue'
    $response = Invoke-WebRequest $zet_dl -OutFile "${scriptPath}\zet.zip"
    verifyFile("${scriptPath}\zet.zip")

    echo "Expanding downloaded file..."
    Expand-Archive -Path "${scriptPath}\zet.zip" -Force -DestinationPath "${buildPath}\service"
    echo "expanded zet.zip file to ${buildPath}\service"
    
    if (Test-Path -Path "${scriptPath}\wintun.zip") {
        echo "using wintun.zip found at ${scriptPath}\wintun.zip"
    } else {
        echo "wintun.zip not found. attempting to download."       
        if($null -eq $env:WINTUN_DL_URL) {
            echo "========================== fetching wintun.dll =========================="
            $WINTUN_DL_URL="https://www.wintun.net/builds/wintun-0.13.zip"
            echo "Beginning to download wintun from ${WINTUN_DL_URL}"
            echo ""
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest $WINTUN_DL_URL -OutFile "${scriptPath}\wintun.zip"
            verifyFile("${scriptPath}\wintun.zip")
            echo "Expanding downloaded file..."
            Expand-Archive -Path "${scriptPath}\wintun.zip" -Force -DestinationPath "${buildPath}\service"
            echo "expanded wintun.zip file to ${buildPath}\service"
        } else {
            echo "WINTUN_DL_URL WAS SET"
        }
    }
} else {
    echo "========================== using locally defined ziti-edge-tunnel =========================="
    $zet_folder=$env:ZITI_EDGE_TUNNEL_BUILD
    echo "local build folder set to: $zet_folder"
    echo ""
    verifyFile("$zet_folder\ziti-edge-tunnel.exe")
    verifyFile("$zet_folder\wintun.dll")
    echo ""
    mkdir "${buildPath}\service" -ErrorAction Ignore > $null
    copy "$zet_folder\ziti-edge-tunnel.exe" -Destination "$buildPath\service" -Force
    copy "$zet_folder\wintun.dll" -Destination "$buildPath\service" -Force
}

Push-Location ${scriptPath}\..

echo "========================== fetching vc++ redist =========================="
$VC_REDIST_URL="https://aka.ms/vs/17/release/vc_redist.x64.exe"
echo "Beginning to download vc++ redist from MS at ${VC_REDIST_URL}"
echo ""
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest $VC_REDIST_URL -OutFile "${buildPath}\VC_redist.x64.exe"
verifyFile("${buildPath}\VC_redist.x64.exe")
echo "========================== fetching vc++ redist complete =========================="

echo "Updating the version for UI and Installer"
.\update-versions.ps1

echo "Building the UI"
msbuild ZitiDesktopEdge.sln /property:Configuration=Release

Pop-Location

$ADV_INST_HOME = "C:\Program Files (x86)\Caphyon\Advanced Installer 21.2.1"
$ADVINST = "${ADV_INST_HOME}\bin\x86\AdvancedInstaller.com"
$ADVPROJECT = "${scriptPath}\ZitiDesktopEdge.aip"

$installerVersion=(Get-Content -Path ${scriptPath}\..\version)
if($null -ne $env:ZITI_DESKTOP_EDGE_VERSION) {
    echo "ZITI_DESKTOP_EDGE_VERSION is set. Using that: ${env:ZITI_DESKTOP_EDGE_VERSION} instead of version found in file ${installerVersion}"
    $installerVersion=$env:ZITI_DESKTOP_EDGE_VERSION
    echo "Version set to: ${installerVersion}"
}
$action = '/SetVersion'

echo "issuing $ADVINST /edit $ADVPROJECT $action $installerVersion (service version: $serviceVersion) - see https://www.advancedinstaller.com/user-guide/set-version.html"
& $ADVINST /edit $ADVPROJECT $action $installerVersion

$action = '/build'
echo "Assembling installer using AdvancedInstaller at: $ADVINST $action $ADVPROJECT"
& $ADVINST $action $ADVPROJECT

$gituser=$(git config user.name)
if($gituser -eq "ziti-ci") {
  echo "detected user [${gituser}]"
  git add DesktopEdge/Properties/AssemblyInfo.cs ZitiDesktopEdge.Client/Properties/AssemblyInfo.cs ZitiUpdateService/Properties/AssemblyInfo.cs Installer/ZitiDesktopEdge.aip
  git commit -m "committing any version changes via ziti-ci"
  git push
} else {
  echo "detected user [${gituser}] which is not ziti-ci - skipping installer commit"
}

$exeAbsPath="${scriptPath}\Output\Ziti Desktop Edge Client-${installerVersion}.exe"

if($null -eq $env:OPENZITI_P12_PASS) {
    echo ""
    echo "Not calling signtool - env:OPENZITI_P12_PASS is not set"
    echo ""
} else {
    echo "adding additional signature to executable with openziti.org signing certificate"\
    echo "RUNNING: $ADV_INST_HOME\third-party\winsdk\x64\signtool" sign /f "${scriptPath}\openziti.p12" /p "${env:OPENZITI_P12_PASS}" /tr http://ts.ssl.com /fd sha512 /td sha512 /as "${exeAbsPath}"
    & "$ADV_INST_HOME\third-party\winsdk\x64\signtool" sign /f "${scriptPath}\openziti.p12" /p "${env:OPENZITI_P12_PASS}" /tr http://ts.ssl.com /fd sha512 /td sha512 /as "${exeAbsPath}"
}
(Get-FileHash "${exeAbsPath}").Hash > "${scriptPath}\Output\Ziti Desktop Edge Client-${installerVersion}.exe.sha256"
echo "========================== build.ps1 completed =========================="

$defaultRootUrl = "https://github.com/openziti/desktop-edge-win/releases/download/"
$defaultStream = "beta"
$defaultPublishedAt = Get-Date
$outputPath = "${installerVersion}.json"
& .\Installer\output-build-json.ps1 -version $installerVersion -url $defaultRootUrl -stream $defaultStream -published_at $defaultPublishedAt -outputPath $outputPath
