# 官網截圖用（docs/website.md §16.4）：用 UI Automation 填拾音的輸入框（\n 是換行）：setnotes.ps1 <視窗標題> <輸入框名稱> <文字>
param([string]$Window, [string]$Name, [string]$Text)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
$procs = @(Get-Process Shiyin | ForEach-Object Id)
$w = $A::RootElement.FindAll($T::Children, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $procs -contains $_.Current.ProcessId -and $_.Current.Name -eq $Window } | Select-Object -First 1
$c = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $Name)), (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
$e = $w.FindFirst($T::Descendants, $c)
if (-not $e) { throw "找不到輸入框 $Name" }
$p = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$p.SetValue($Text.Replace('\n', "`r"))
"set"
