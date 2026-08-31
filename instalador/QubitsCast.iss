; Instalador do QubitsCast.
; Compilado por build.ps1 — não rodar direto sem antes publicar o app.
;
; Instala na pasta do usuário de propósito: assim não pede permissão de administrador.

#define MeuNome "QubitsCast"
#define MeuVersao "1.0.3"
#define MeuAutor "QubitsLab"
#define MeuExe "QubitsCast.exe"

[Setup]
AppId={{7C4E1B2A-9D3F-4A56-B8E1-0FA51C4B7D22}
AppName={#MeuNome}
AppVersion={#MeuVersao}
AppVerName={#MeuNome} {#MeuVersao}
AppPublisher={#MeuAutor}
VersionInfoVersion={#MeuVersao}

DefaultDirName={localappdata}\Programs\{#MeuNome}
DefaultGroupName={#MeuNome}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

; Sem UAC: instala só para quem está usando o computador.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=saida
OutputBaseFilename=QubitsCast-instalador
SetupIconFile=..\app\Recursos\qubitscast.ico
UninstallDisplayIcon={app}\{#MeuExe}
WizardStyle=modern

Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "atalhodesktop"; Description: "Criar um atalho na área de trabalho"; \
    GroupDescription: "Atalhos:"

[Files]
Source: "app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "ffmpeg\ffmpeg.exe"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MeuNome}"; Filename: "{app}\{#MeuExe}"
Name: "{group}\Desinstalar o {#MeuNome}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MeuNome}"; Filename: "{app}\{#MeuExe}"; Tasks: atalhodesktop

[Registry]
; Faz o link de convite (qubitscast://) abrir o aplicativo.
Root: HKCU; Subkey: "Software\Classes\qubitscast"; ValueType: string; \
    ValueName: ""; ValueData: "URL:QubitsCast"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\qubitscast"; ValueType: string; \
    ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\qubitscast\DefaultIcon"; ValueType: string; \
    ValueName: ""; ValueData: """{app}\{#MeuExe}"",0"
Root: HKCU; Subkey: "Software\Classes\qubitscast\shell\open\command"; ValueType: string; \
    ValueName: ""; ValueData: """{app}\{#MeuExe}"" ""%1"""

[Run]
Filename: "{app}\{#MeuExe}"; Description: "Abrir o {#MeuNome} agora"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\QubitsCast"

[Code]
// Fechar o app antes de instalar por cima evita "arquivo em uso" no meio da cópia.
function InitializeSetup(): Boolean;
var
  Codigo: Integer;
begin
  Exec('taskkill.exe', '/F /IM {#MeuExe}', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
  Result := True;
end;

function InitializeUninstall(): Boolean;
var
  Codigo: Integer;
begin
  Exec('taskkill.exe', '/F /IM {#MeuExe}', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
  Result := True;
end;
