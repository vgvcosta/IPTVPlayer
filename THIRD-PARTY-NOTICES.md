# Componentes de terceiros

O código deste projeto está sob a licença MIT (ver [LICENSE](LICENSE)). A aplicação usa e, na
versão publicada, distribui os seguintes componentes, cada um com a sua licença:

| Componente | Licença | Notas |
|---|---|---|
| [LibVLC / VLC](https://www.videolan.org/vlc/libvlc.html) (`VideoLAN.LibVLC.Windows`) | LGPL-2.1-or-later (alguns plugins GPL-2.0) | Biblioteca nativa de reprodução, distribuída sem modificações em `libvlc\`. O código-fonte está em https://code.videolan.org/videolan/vlc |
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) / LibVLCSharp.WPF | LGPL-2.1-or-later | Ligação .NET ao LibVLC |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | |
| [Dapper](https://github.com/DapperLib/Dapper) | Apache-2.0 | |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) / SQLitePCLRaw / SQLite | MIT / Apache-2.0 / domínio público | |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) | MIT | |
| [.NET runtime](https://github.com/dotnet/runtime) (versão self-contained) | MIT | |

As bibliotecas LGPL são usadas por ligação dinâmica e podem ser substituídas por outras versões
compatíveis na pasta da aplicação publicada.
