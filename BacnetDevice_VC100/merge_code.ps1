# 이 줄을 맨 위에 추가하세요
$OutputEncoding = [System.Text.Encoding]::UTF8
# 프로젝트 내 주요 파일들을 하나로 합치는 스크립트
$outputFile = "Project_Summary.txt"
$includeExtensions = @("*.cs", "*.csproj", "*.xml")
$excludeFolders = @("bin", "obj", "Properties", "Packages", ".vs")

Write-Host "--- 코드 병합 작업을 시작합니다 ---" -ForegroundColor Cyan

# 초기화 (파일이 이미 있으면 새로 만듦)
"" > $outputFile

$files = Get-ChildItem -Recurse -Include $includeExtensions | Where-Object { 
    $path = $_.FullName
    $keep = $true
    foreach ($exclude in $excludeFolders) {
        if ($path -like "*\$exclude\*") { $keep = $false; break }
    }
    $keep
}

foreach ($file in $files) {
    $relativeName = $file.FullName.Replace($(Get-Location).Path, "")
    Write-Host "병합 중: $relativeName" -ForegroundColor Yellow
    
    "------------------------------------------" >> $outputFile
    "파일명: $relativeName" >> $outputFile
    "------------------------------------------" >> $outputFile
    Get-Content $file.FullName >> $outputFile
    "`n`n" >> $outputFile
}

Write-Host "`n--- 병합 완료! ---" -ForegroundColor Green
Write-Host "결과 파일: $outputFile" -ForegroundColor White
Read-Host "종료하려면 Enter 키를 누르세요"