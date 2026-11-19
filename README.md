# MultiBar

Barras de tarefas extras para o Windows 11, uma por monitor (ou várias), com apps fixados, grupos de apps e janelas abertas — no visual da barra nativa.

> 🇺🇸 Extra Windows 11 taskbars for multi-monitor setups: pinned apps, app groups, running windows, clock and tray, styled like the native taskbar. UI in Portuguese (pt-BR).

## Recursos

- **Uma barra por monitor** (ou mais), no topo ou na base, alinhada ao centro ou à esquerda. Reserva espaço na tela como a barra do Windows (AppBar).
- **Apps fixados** por executável, atalho `.lnk`, arquivo ou AUMID (apps da Store), com ícone personalizado (`.ico`/`.png`).
- **Grupos de apps**: um botão abre vários apps; opcionalmente minimiza/restaura todas as janelas do grupo juntas.
- **Perfis separados**: itens podem reconhecer janelas por `--user-data-dir` (ex.: dois perfis do Chrome/Edge em botões diferentes).
- **Janelas abertas** do monitor aparecem na barra dele, com miniaturas ao passar o mouse.
- **Listas de atalhos (Recentes)** do Windows no menu de contexto.
- **Relógio**, **botão Iniciar** e **área de notificação** (rede, volume, notificações) opcionais.
- **Finalizar tarefa** pelo menu, como no Gerenciador de Tarefas.
- **Esconder a barra do Windows** enquanto o MultiBar roda (restaurada ao sair, inclusive após crash).
- Acompanha o tema claro/escuro e a cor de destaque do Windows, com efeito acrílico.
- Iniciar com o Windows.

## Requisitos

- Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Instalação

Baixe o `MultiBar.exe` da página de [Releases](https://github.com/BN0640/windows-11-multibar/releases) (ou do artefato do último build em **Actions**) e execute. Não há instalador.

## Uso

- Clique com o botão direito numa área vazia da barra para adicionar apps, criar grupos, configurar a barra ou criar novas barras.
- Arraste apps (exe, atalhos) para a barra para fixá-los.
- Clique com o botão direito num item para fixar/desafixar, mover, agrupar ou fechar janelas.

## Configuração

Tudo fica em `%APPDATA%\MultiBar\config.json` (acessível pelo menu *Abrir arquivo de configuração*). Erros são registrados em `%APPDATA%\MultiBar\error.log`.

## Compilar

```powershell
dotnet build MultiBar.sln -c Release
```

Executável único:

```powershell
dotnet publish MultiBar/MultiBar.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Estrutura

| Arquivo | Função |
|---|---|
| `App.xaml.cs` | Inicialização, instância única, ciclo de vida das barras |
| `BarWindow.cs` | A barra: AppBar, layout, menus, edição |
| `BarButton.cs` | Botão de app/grupo/janela |
| `WindowTracker.cs` | Enumeração e eventos das janelas abertas |
| `ThumbnailPopup.cs` | Miniaturas ao vivo (DWM) |
| `JumpLists.cs` | Lista "Recentes" do Windows |
| `Apps.cs` | Lançamento de apps, ícones, início automático |
| `SystemButtons.cs` | Iniciar, relógio, bandeja |
| `WindowsTaskbar.cs` | Esconder/restaurar a barra nativa |
| `Monitors.cs`, `Theme.cs`, `Native.cs` | Monitores, tema e interop Win32 |
| `Config.cs` | Modelo e persistência do `config.json` |

## Licença

[MIT](LICENSE)
