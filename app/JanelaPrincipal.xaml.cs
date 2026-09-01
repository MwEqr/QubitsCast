using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QubitsCast.Core;

namespace QubitsCast;

public sealed class ItemParticipante
{
    public string Nome { get; init; } = "";
    public string Inicial { get; init; } = "?";
    public string Marca { get; init; } = "";
    public Brush Cor { get; init; } = Brushes.Gray;
}

public partial class JanelaPrincipal : Window
{
    private static readonly Color[] _paleta =
    {
        Color.FromRgb(0x5B, 0x8C, 0xFF), Color.FromRgb(0x3F, 0xBF, 0x7F),
        Color.FromRgb(0xE0, 0x8A, 0x3C), Color.FromRgb(0xB4, 0x6B, 0xE8),
        Color.FromRgb(0xE5, 0x64, 0x8A), Color.FromRgb(0x3F, 0xB6, 0xBF),
    };

    private readonly Ajustes _ajustes = Ajustes.Carregar();
    private readonly ObservableCollection<ItemParticipante> _participantes = [];
    private List<Tela> _telas = [];
    private List<Fonte> _fontes = [];
    private List<AppComSom> _appsComSom = [];

    /// <summary>Opções do seletor de som: rótulo, de onde vem, qual programa, e se tem som.</summary>
    private readonly List<(string Rotulo, FonteSom Fonte, int Pid, bool ComSom)> _opcoesSom = [];

    private Sinal? _sinal;
    private Transmissor? _transmissor;
    private Receptor? _receptor;
    private CapturaAudio? _somDoSistema;
    private CapturaAudio? _microfone;
    private ReproducaoAudio? _reproducao;

    private WriteableBitmap? _tela;
    private bool _renderizandoLigado;
    private int _idTransmissorAtual;
    private string _linkAtual = "";
    private DispatcherTimer? _tempoAviso;
    private bool _semPlaca;
    private bool _servidorConferido;
    private VersaoPublicada? _versaoNova;
    private InfoTransmissao? _formatoAtual;
    private int _reducoes;

    public JanelaPrincipal()
    {
        InitializeComponent();

        ListaParticipantes.ItemsSource = _participantes;
        CampoApelido.Text = _ajustes.Apelido;
        ControleVolume.Value = _ajustes.VolumeSaida;

        MontarSeletores();

        App.AoReceberConvite += codigo => Dispatcher.BeginInvoke(() => EntrarPorConvite(codigo));

        Loaded += AoCarregar;
        Closing += AoFechar;
    }

    private async void AoCarregar(object? remetente, RoutedEventArgs e)
    {
        CampoApelido.Focus();
        CampoApelido.CaretIndex = CampoApelido.Text.Length;

        // A sondagem de placa e captura roda fora da interface: leva alguns segundos na
        // primeira vez, e o resultado fica guardado para as próximas.
        _ = Task.Run(() =>
        {
            var cap = Ffmpeg.Detectar();
            var telas = Telas.CasarComSaidasDaPlaca(_telas);
            Dispatcher.BeginInvoke(() =>
            {
                TituloBarra.Text = "usando " + cap.Resumo;
                _semPlaca = !cap.EncoderPorPlaca;
                AtualizarRotulosQualidade();

                _telas = telas;
                MontarFontes();
            });
        });

        await VerificarServidorAsync();

        if (!string.IsNullOrEmpty(App.ConviteInicial))
            EntrarPorConvite(App.ConviteInicial!);

        _ = ProcurarAtualizacaoAsync();
    }

    // ================================================================== atualização

    /// <summary>
    /// Procura versão nova ao abrir e depois de tempos em tempos. Nunca instala sozinho:
    /// quem decide a hora é quem está usando — pode estar no meio de uma transmissão.
    /// </summary>
    private async Task ProcurarAtualizacaoAsync()
    {
        Atualizacao.LimparAntigos();

        while (true)
        {
            try
            {
                var achada = await Atualizacao.ProcurarAsync(_ajustes.Servidor);
                if (achada is not null)
                {
                    _versaoNova = achada;
                    BotaoAtualizar.Content = "Atualizar para " + achada.Versao;
                    BotaoAtualizar.Visibility = Visibility.Visible;
                    MostrarAviso($"Saiu a versão {achada.Versao}" +
                                 (string.IsNullOrWhiteSpace(achada.Notas) ? "" : ": " + achada.Notas));
                }
            }
            catch (Exception e) { Registro.Falha("ProcurarAtualizacao", e); }

            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    private async void Atualizar_Clique(object remetente, RoutedEventArgs e)
    {
        var versao = _versaoNova;
        if (versao is null) return;

        if (_transmissor?.Ativo == true)
        {
            MostrarAviso("Pare a transmissão antes de atualizar.");
            return;
        }

        BotaoAtualizar.IsEnabled = false;
        BotaoAtualizar.Content = "Baixando…";

        var andamento = new Progress<double>(p =>
            BotaoAtualizar.Content = $"Baixando… {p:0}%");

        var arquivo = await Atualizacao.BaixarAsync(versao, andamento);
        if (arquivo is null)
        {
            BotaoAtualizar.IsEnabled = true;
            BotaoAtualizar.Content = "Atualizar para " + versao.Versao;
            MostrarAviso("Não consegui baixar a atualização. Tente de novo daqui a pouco.");
            return;
        }

        BotaoAtualizar.Content = "Instalando…";
        DesmontarSala();

        if (!Atualizacao.Instalar(arquivo))
        {
            BotaoAtualizar.IsEnabled = true;
            BotaoAtualizar.Content = "Atualizar para " + versao.Versao;
            MostrarAviso("Não consegui abrir o instalador da atualização.");
            return;
        }

        // O instalador troca arquivos que estão em uso; o app precisa sair da frente.
        await Task.Delay(600);
        Application.Current.Shutdown();
    }

    private void MontarSeletores()
    {
        _telas = Telas.Listar();
        MontarFontes();
        MontarSeletorDeSom();

        AtualizarRotulosQualidade();
        var atual = Array.FindIndex(Padroes.TodasQualidades,
            q => q.Largura == _ajustes.Largura && q.Fps == _ajustes.Fps);
        SeletorQualidade.SelectedIndex = atual >= 0 ? atual : 3;
    }

    /// <summary>
    /// Escreve ao lado de cada qualidade o que ela custa de internet e o que não cabe
    /// na conexão medida. Sem isso a pessoa escolhe 4K num link de 3 Mb/s e a imagem
    /// trava do outro lado sem nenhuma pista do motivo.
    /// </summary>
    private void AtualizarRotulosQualidade()
    {
        var semPlaca = _semPlaca;
        var velocidade = Medidor.Ultima;

        var itens = new List<string>();
        for (int i = 0; i < Padroes.TodasQualidades.Length; i++)
        {
            var q = Padroes.TodasQualidades[i];
            // O aviso substitui o consumo em vez de somar a ele: os dois juntos não cabem
            // na largura do seletor e o texto sai cortado no meio de uma palavra.
            string texto;
            if (velocidade is not null && !Medidor.Cabe(i, velocidade.SubidaMbps))
                texto = $"{q.Rotulo} · não cabe";
            else if (semPlaca && (q.Largura >= 2560 || (q.Largura >= 1920 && q.Fps >= 60)))
                texto = $"{q.Rotulo} · pesado";
            else
                texto = $"{q.Rotulo} · {q.Bitrate} Mb/s";

            itens.Add(texto);
        }

        var escolhido = SeletorQualidade.SelectedIndex;
        SeletorQualidade.ItemsSource = itens;
        SeletorQualidade.SelectedIndex = Math.Clamp(escolhido, 0, itens.Count - 1);
    }

    /// <summary>Monta a lista do que dá para transmitir: os monitores e as janelas abertas.</summary>
    private void MontarFontes()
    {
        var escolhidaAntes = FonteEscolhida()?.Chave ?? _ajustes.FonteChave;

        _fontes = Fontes.Listar(_telas);
        SeletorFonte.ItemsSource = _fontes.Select(f => f.Rotulo).ToList();

        // Tenta manter o que estava escolhido; janela fechada volta para o monitor principal.
        var indice = _fontes.FindIndex(f => f.Chave == escolhidaAntes);
        if (indice < 0) indice = _fontes.FindIndex(f => !f.EhJanela);
        SeletorFonte.SelectedIndex = Math.Max(0, indice);
    }

    private Fonte? FonteEscolhida()
        => _fontes.ElementAtOrDefault(SeletorFonte.SelectedIndex);

    /// <summary>
    /// Monta as opções de som. "Só o som de um programa" só entra quando o Windows tem a
    /// função — em vez de deixar a pessoa escolher e falhar na hora de transmitir.
    /// </summary>
    private void MontarSeletorDeSom()
    {
        _opcoesSom.Clear();
        _opcoesSom.Add(("Sem som", FonteSom.Computador, 0, false));
        _opcoesSom.Add(("Som do computador", FonteSom.Computador, 0, true));

        if (AudioPorApp.Suportado)
        {
            _appsComSom = AudioPorApp.Listar();
            foreach (var a in _appsComSom)
                _opcoesSom.Add(($"Só o som de {a.Nome}", FonteSom.Aplicativo, a.Pid, true));
            SeletorAudio.ToolTip = "Que som vai junto com a imagem";
        }
        else
        {
            SeletorAudio.ToolTip = AudioPorApp.MotivoIndisponivel;
        }

        SeletorAudio.ItemsSource = _opcoesSom.Select(o => o.Rotulo).ToList();

        var indice = _ajustes.ModoSom switch
        {
            0 => 0,
            2 => Math.Max(1, _opcoesSom.FindIndex(o =>
                     o.Fonte == FonteSom.Aplicativo &&
                     o.Rotulo.EndsWith(_ajustes.ProgramaDoSom, StringComparison.OrdinalIgnoreCase))),
            _ => 1,
        };
        SeletorAudio.SelectedIndex = Math.Clamp(indice, 0, _opcoesSom.Count - 1);
    }

    private void AtualizarFontes_Clique(object remetente, RoutedEventArgs e)
    {
        _telas = Telas.CasarComSaidasDaPlaca(Telas.Listar());
        MontarFontes();
        MontarSeletorDeSom();
        MostrarAviso("Lista de telas e janelas atualizada.");
    }

    /// <summary>Mede a internet e deixa a qualidade escolhida no que ela aguenta.</summary>
    private async Task MedirInternetAsync()
    {
        var velocidade = await Medidor.MedirAsync(_ajustes.Servidor);
        if (velocidade is null || velocidade.Vazia) return;

        AtualizarRotulosQualidade();

        var melhor = Medidor.MelhorQualidade(velocidade.SubidaMbps);
        if (SeletorQualidade.SelectedIndex > melhor)
        {
            SeletorQualidade.SelectedIndex = melhor;
            MostrarAviso($"Sua internet sobe {velocidade.SubidaMbps:0.#} Mb/s. " +
                         $"Deixei em {Padroes.TodasQualidades[melhor].Rotulo} para a imagem não travar.");
        }
        else
        {
            MostrarAviso($"Sua internet sobe {velocidade.SubidaMbps:0.#} Mb/s " +
                         $"e baixa {velocidade.DescidaMbps:0.#} Mb/s.");
        }
    }

    private async Task VerificarServidorAsync()
    {
        TextoServidor.Text = "verificando o servidor…";
        LuzServidor.Fill = (Brush)FindResource("Apagado");

        var ok = await Sinal.ServidorRespondeAsync(_ajustes.Servidor);

        // Endereço guardado que parou de responder não pode deixar o app inútil: se o
        // servidor oficial atende, é ele que vale, e a preferência é corrigida em disco.
        if (!ok && _ajustes.Servidor != Padroes.ServidorPadrao &&
            await Sinal.ServidorRespondeAsync(Padroes.ServidorPadrao))
        {
            Registro.Escrever($"servidor guardado ({_ajustes.Servidor}) não respondeu; " +
                              $"voltando para {Padroes.ServidorPadrao}");
            _ajustes.Servidor = Padroes.ServidorPadrao;
            _ajustes.Salvar();
            ok = true;
        }

        if (ok)
        {
            LuzServidor.Fill = (Brush)FindResource("Ok");
            TextoServidor.Text = "servidor conectado";
        }
        else
        {
            LuzServidor.Fill = (Brush)FindResource("Perigo");
            TextoServidor.Text = "servidor fora do ar — não dá para criar sala agora";
        }
        BotaoCriar.IsEnabled = ok;
        BotaoEntrar.IsEnabled = ok;
        _servidorConferido = true;
    }

    // ================================================================== entrada

    private async void Criar_Clique(object remetente, RoutedEventArgs e)
    {
        var apelido = Apelido();
        if (apelido is null) return;
        if (!await ConectarAsync()) return;
        _sinal!.CriarSala(apelido, $"Sala de {apelido}");
    }

    private async void Entrar_Clique(object remetente, RoutedEventArgs e)
    {
        var apelido = Apelido();
        if (apelido is null) return;

        var codigo = new string(CampoCodigo.Text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (codigo.Length < 4)
        {
            MostrarAviso("Digite o código da sala (aquele que vem no link do convite).");
            CampoCodigo.Focus();
            return;
        }

        if (!await ConectarAsync()) return;
        _sinal!.EntrarNaSala(codigo, apelido);
    }

    private void CampoCodigo_Tecla(object remetente, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Entrar_Clique(remetente, new RoutedEventArgs());
    }

    private async void EntrarPorConvite(string codigo)
    {
        Registro.Escrever($"entrando pelo convite: {codigo}");
        CampoCodigo.Text = codigo;
        if (PainelSala.Visibility == Visibility.Visible) SairDaSala();

        var apelido = Apelido();
        if (apelido is null) return;

        // O convite chega antes da checagem terminar quando o app é aberto pelo link.
        if (!_servidorConferido) await VerificarServidorAsync();

        if (!await ConectarAsync()) return;
        _sinal!.EntrarNaSala(codigo, apelido);
    }

    private string? Apelido()
    {
        var a = CampoApelido.Text.Trim();
        if (a.Length < 2)
        {
            MostrarAviso("Escreva seu nome para os outros saberem quem é você.");
            CampoApelido.Focus();
            return null;
        }
        _ajustes.Apelido = a;
        _ajustes.Salvar();
        return a;
    }

    private async Task<bool> ConectarAsync()
    {
        DesmontarSala();

        _sinal = new Sinal(_ajustes.Servidor);
        _sinal.AoEntrar += s => Dispatcher.BeginInvoke(() => MostrarSala(s));
        _sinal.AoAtualizar += s => Dispatcher.BeginInvoke(() => AtualizarSala(s));
        _sinal.AoErro += m => Dispatcher.BeginInvoke(() => MostrarAviso(m));
        _sinal.AoRecado += (quem, texto) => Dispatcher.BeginInvoke(() => MostrarAviso($"{quem}: {texto}"));
        _sinal.AoCair += () => Dispatcher.BeginInvoke(AoPerderConexao);
        _sinal.AoMidia += TratarMidia;   // de propósito fora da interface: é o caminho quente

        BotaoCriar.IsEnabled = BotaoEntrar.IsEnabled = false;
        var ok = await _sinal.ConectarAsync();
        BotaoCriar.IsEnabled = BotaoEntrar.IsEnabled = true;

        if (!ok) MostrarAviso("Não consegui falar com o servidor. Confira sua internet.");
        return ok;
    }

    // ================================================================== sala

    private void MostrarSala(EstadoSala sala)
    {
        Registro.Escrever($"entrei na sala {sala.Codigo} como id {sala.Voce} " +
                          $"({sala.Participantes.Count} pessoa(s))");
        PainelEntrada.Visibility = Visibility.Collapsed;
        PainelSala.Visibility = Visibility.Visible;

        _reproducao = new ReproducaoAudio();
        _reproducao.Iniciar();
        _reproducao.Volume = (float)(ControleVolume.Value / 100.0);

        _transmissor = new Transmissor(_sinal!);
        _transmissor.AoMedir += s => Dispatcher.BeginInvoke(() =>
            AtualizarSelo($"enviando  {s.Fps} fps  ·  {s.Mbps:0.0} Mb/s"));
        _transmissor.AoFalhar += m => Dispatcher.BeginInvoke(() =>
        {
            MostrarAviso(m);
            PararTransmissao();
        });
        _transmissor.AoAvisar += m => Dispatcher.BeginInvoke(() => MostrarAviso(m));

        AtualizarSala(sala);

        // Só quem entrou numa sala vai transmitir; medir antes disso seria gasto à toa.
        if (Medidor.Ultima is null) _ = MedirInternetAsync();
        else AtualizarRotulosQualidade();
    }

    private void AtualizarSala(EstadoSala sala)
    {
        _linkAtual = sala.Link;
        NomeSala.Text = sala.Nome;
        CodigoSala.Text = sala.Codigo;
        TituloBarra.Text = $"sala {sala.Codigo}";

        _participantes.Clear();
        foreach (var p in sala.Participantes)
        {
            var marcas = new List<string>();
            if (p.Transmitindo) marcas.Add("ao vivo");
            if (p.Microfone) marcas.Add("mic");
            if (p.Id == sala.Voce) marcas.Add("você");

            _participantes.Add(new ItemParticipante
            {
                Nome = p.Apelido,
                Inicial = string.IsNullOrEmpty(p.Apelido) ? "?" : p.Apelido[..1].ToUpperInvariant(),
                Marca = string.Join(" · ", marcas),
                Cor = new SolidColorBrush(_paleta[Math.Abs(p.Apelido.GetHashCode()) % _paleta.Length]),
            });
        }
        TituloParticipantes.Text = sala.Participantes.Count == 1
            ? "1 PESSOA NA SALA"
            : $"{sala.Participantes.Count} PESSOAS NA SALA";

        AplicarTransmissao(sala);
    }

    /// <summary>Liga ou desliga a exibição conforme quem está transmitindo agora.</summary>
    private void AplicarTransmissao(EstadoSala sala)
    {
        var t = sala.Transmissao;
        bool souEu = t is not null && t.Id == sala.Voce;

        BotaoTransmitir.Content = _transmissor?.Ativo == true
            ? "Parar de transmitir"
            : "Transmitir minha tela";
        BotaoTransmitir.Style = (Style)FindResource(
            _transmissor?.Ativo == true ? "BotaoPerigo" : "BotaoPrimario");

        // Outra pessoa está transmitindo: os controles de captura ficam fora de alcance.
        bool alguemMais = t is not null && !souEu;
        BotaoTransmitir.IsEnabled = !alguemMais;
        SeletorFonte.IsEnabled = SeletorAudio.IsEnabled = SeletorQualidade.IsEnabled =_transmissor?.Ativo != true;

        if (t is null || souEu)
        {
            PararExibicao();
            var quem = sala.Participantes.Count <= 1
                ? "Mande o link do convite para alguém entrar."
                : "Clique em “Transmitir minha tela” para começar.";
            TextoVazio.Text = souEu ? "Você está transmitindo" : "Ninguém está transmitindo ainda";
            TextoVazio2.Text = souEu
                ? "Quem está na sala já está vendo sua tela."
                : quem;
            return;
        }

        if (_receptor is not null && _idTransmissorAtual == t.Id &&
            _receptor.Ativo && _receptor.Largura > 0)
            return;   // já está exibindo essa mesma transmissão

        _idTransmissorAtual = t.Id;
        _reducoes = 0;   // transmissão nova começa do tamanho cheio
        var nome = sala.Participantes.FirstOrDefault(p => p.Id == t.Id)?.Apelido ?? "alguém";
        TextoVazio.Text = $"Conectando na tela de {nome}…";
        TextoVazio2.Text = $"{t.Largura}×{t.Altura} a {t.Fps} quadros por segundo";
        IniciarExibicao(t);
    }

    // ================================================================== transmissão

    private void Transmitir_Clique(object remetente, RoutedEventArgs e)
    {
        if (_transmissor is null) return;

        if (_transmissor.Ativo) { PararTransmissao(); return; }

        var fonte = FonteEscolhida();
        if (fonte is null) { MostrarAviso("Escolha o que você quer transmitir."); return; }

        var q = Padroes.TodasQualidades[Math.Max(0, SeletorQualidade.SelectedIndex)];

        _ajustes.FonteChave = fonte.Chave;
        _ajustes.MonitorIndice = fonte.EhJanela ? _ajustes.MonitorIndice : fonte.IndiceDxgi;
        _ajustes.Largura = q.Largura;
        _ajustes.Fps = q.Fps;
        _ajustes.Bitrate = q.Bitrate;
        _ajustes.Salvar();

        if (!_transmissor.Iniciar(fonte, q.Largura, q.Fps, q.Bitrate)) return;

        IniciarSomDaTransmissao();

        BotaoTransmitir.Content = "Parar de transmitir";
        BotaoTransmitir.Style = (Style)FindResource("BotaoPerigo");
        SeletorFonte.IsEnabled = SeletorAudio.IsEnabled = SeletorQualidade.IsEnabled =false;
        Selo.Visibility = Visibility.Visible;
        AtualizarSelo("iniciando…");
        AvisoVazio.Visibility = Visibility.Visible;
        TextoVazio.Text = "Você está transmitindo";
        TextoVazio2.Text = "Quem está na sala já está vendo sua tela.";
    }

    /// <summary>
    /// Liga o som escolhido junto com a imagem. Quando a pessoa pediu o som de um programa
    /// e o Windows não deixa, cai para o som do computador e explica — melhor do que
    /// transmitir mudo sem avisar.
    /// </summary>
    private void IniciarSomDaTransmissao()
    {
        var opcao = _opcoesSom.ElementAtOrDefault(SeletorAudio.SelectedIndex);
        if (opcao == default || !opcao.ComSom)
        {
            _ajustes.ModoSom = 0;
            _ajustes.Salvar();
            return;
        }

        _ajustes.ModoSom = opcao.Fonte == FonteSom.Aplicativo ? 2 : 1;
        _ajustes.ProgramaDoSom = opcao.Fonte == FonteSom.Aplicativo
            ? _appsComSom.FirstOrDefault(a => a.Pid == opcao.Pid)?.Nome ?? ""
            : "";
        _ajustes.Salvar();

        _somDoSistema = new CapturaAudio(opcao.Fonte,
            pacote => _sinal?.EnviarMidia(Pacote.AudioTela, pacote), opcao.Pid);

        if (_somDoSistema.Iniciar()) return;

        var recado = _somDoSistema.Recado;
        _somDoSistema.Dispose();

        if (opcao.Fonte == FonteSom.Aplicativo)
        {
            _somDoSistema = new CapturaAudio(FonteSom.Computador,
                pacote => _sinal?.EnviarMidia(Pacote.AudioTela, pacote));
            if (_somDoSistema.Iniciar())
            {
                MostrarAviso(recado ?? "Segui com o som do computador inteiro.");
                return;
            }
        }

        _somDoSistema = null;
        MostrarAviso(recado ?? "A tela está indo, mas não consegui capturar o som.");
    }

    private void PararTransmissao()
    {
        _transmissor?.Parar();
        _somDoSistema?.Parar();
        _somDoSistema = null;

        BotaoTransmitir.Content = "Transmitir minha tela";
        BotaoTransmitir.Style = (Style)FindResource("BotaoPrimario");
        SeletorFonte.IsEnabled = SeletorAudio.IsEnabled = SeletorQualidade.IsEnabled =true;
        Selo.Visibility = Visibility.Collapsed;
        TextoVazio.Text = "Ninguém está transmitindo ainda";
        TextoVazio2.Text = "Clique em “Transmitir minha tela” para começar.";
    }

    // ================================================================== exibição

    private void IniciarExibicao(InfoTransmissao t, int tetoManual = 0)
    {
        PararExibicao();

        // Decodificar em tamanho maior que a janela só gastaria processador à toa.
        int teto = tetoManual > 0 ? tetoManual : (ActualWidth > 1920 ? 2560 : 1920);
        int largura = Math.Min(t.Largura, teto);
        int altura = t.Largura > 0
            ? (int)Math.Round((double)largura * t.Altura / t.Largura)
            : t.Altura;
        largura -= largura % 2;
        altura -= altura % 2;

        _formatoAtual = t;

        _receptor = new Receptor();
        _receptor.AoMedir += s => Dispatcher.BeginInvoke(() =>
            AtualizarSelo($"recebendo  {s.Fps} fps  ·  {s.Mbps:0.0} Mb/s" +
                          (s.Atrasados > 0 ? $"  ·  {s.Atrasados} perdidos" : "")));
        _receptor.AoNaoAcompanhar += () => Dispatcher.BeginInvoke(ReduzirExibicao);
        _receptor.AoPrimeiroQuadro += () => Dispatcher.BeginInvoke(() =>
        {
            AvisoVazio.Visibility = Visibility.Collapsed;
            Video.Visibility = Visibility.Visible;
            Selo.Visibility = Visibility.Visible;
        });

        if (!_receptor.Iniciar(largura, altura))
        {
            MostrarAviso("Não consegui abrir o vídeo desta transmissão.");
            return;
        }

        _tela = new WriteableBitmap(largura, altura, 96, 96, PixelFormats.Bgra32, null);
        Video.Source = _tela;

        if (!_renderizandoLigado)
        {
            CompositionTarget.Rendering += AoRenderizar;
            _renderizandoLigado = true;
        }
    }

    /// <summary>
    /// Quando a máquina de quem assiste não dá conta, diminui a imagem em vez de deixar
    /// travar. Vale só duas vezes: além disso o problema não é tamanho, é outra coisa,
    /// e continuar encolhendo só entregaria uma imagem ruim sem resolver nada.
    /// </summary>
    private void ReduzirExibicao()
    {
        if (_reducoes >= 2 || _receptor is null || _formatoAtual is null) return;

        var novoTeto = Math.Max(640, _receptor.Largura * 2 / 3);
        if (novoTeto >= _receptor.Largura) return;

        _reducoes++;
        Registro.Escrever($"reduzindo a exibição de {_receptor.Largura} para {novoTeto} de largura");
        IniciarExibicao(_formatoAtual, novoTeto);

        MostrarAviso("Seu computador não estava acompanhando. " +
                     "Diminuí o tamanho da imagem para ela não travar.");
    }

    private void PararExibicao()
    {
        if (_renderizandoLigado)
        {
            CompositionTarget.Rendering -= AoRenderizar;
            _renderizandoLigado = false;
        }
        _receptor?.Parar();
        _receptor = null;
        _idTransmissorAtual = 0;

        Video.Visibility = Visibility.Collapsed;
        Video.Source = null;
        _tela = null;
        AvisoVazio.Visibility = Visibility.Visible;
        if (_transmissor?.Ativo != true) Selo.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Pinta no ritmo do monitor. Puxar o quadro aqui, em vez de empurrar da rede,
    /// evita que uma rajada de quadros atropele a interface.
    /// </summary>
    private void AoRenderizar(object? remetente, EventArgs e)
    {
        var r = _receptor;
        var alvo = _tela;
        if (r is null || alvo is null) return;

        var quadro = r.PegarQuadro();
        if (quadro is null) return;

        try
        {
            int esperado = alvo.PixelWidth * alvo.PixelHeight * 4;
            if (quadro.Length < esperado) return;

            alvo.Lock();
            System.Runtime.InteropServices.Marshal.Copy(quadro, 0, alvo.BackBuffer, esperado);
            alvo.AddDirtyRect(new Int32Rect(0, 0, alvo.PixelWidth, alvo.PixelHeight));
        }
        catch (Exception ex) { Registro.Falha("AoRenderizar", ex); }
        finally
        {
            try { alvo.Unlock(); } catch { }
        }
    }

    // ================================================================== mídia recebida

    private void TratarMidia(byte tipo, byte origem, byte[] dados)
    {
        try
        {
            switch (tipo)
            {
                case Pacote.VideoParam:
                case Pacote.VideoChave:
                case Pacote.VideoInter:
                    _receptor?.Alimentar(tipo, dados);
                    break;

                case Pacote.AudioTela:
                    _reproducao?.Alimentar(1000 + origem, estereo: true, dados);
                    break;

                case Pacote.AudioVoz:
                    _reproducao?.Alimentar(origem, estereo: false, dados);
                    break;
            }
        }
        catch (Exception e) { Registro.Falha("TratarMidia", e); }
    }

    // ================================================================== controles

    private void Microfone_Clique(object remetente, RoutedEventArgs e)
    {
        if (_microfone is not null)
        {
            _microfone.Parar();
            _microfone = null;
            _sinal?.AnunciarMicrofone(false);
            BotaoMicrofone.Content = "Microfone desligado";
            BotaoMicrofone.Style = (Style)FindResource("BotaoBase");
            return;
        }

        _microfone = new CapturaAudio(FonteSom.Microfone,
            pacote => _sinal?.EnviarMidia(Pacote.AudioVoz, pacote));

        if (!_microfone.Iniciar())
        {
            _microfone = null;
            MostrarAviso("Não achei um microfone disponível.");
            return;
        }

        _sinal?.AnunciarMicrofone(true);
        BotaoMicrofone.Content = "Microfone ligado";
        BotaoMicrofone.Style = (Style)FindResource("BotaoPrimario");
    }

    private void Volume_Mudou(object remetente, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_reproducao is not null) _reproducao.Volume = (float)(e.NewValue / 100.0);
        _ajustes.VolumeSaida = (int)e.NewValue;
    }

    private void CopiarLink_Clique(object remetente, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_linkAtual)) return;
        try
        {
            Clipboard.SetText(_linkAtual);
            MostrarAviso("Link copiado. É só colar no WhatsApp, no Discord, onde quiser.");
        }
        catch (Exception ex)
        {
            Registro.Falha("CopiarLink", ex);
            MostrarAviso("Não consegui copiar. O link é: " + _linkAtual);
        }
    }

    private void Sair_Clique(object remetente, RoutedEventArgs e) => SairDaSala();

    private void SairDaSala()
    {
        DesmontarSala();
        PainelSala.Visibility = Visibility.Collapsed;
        PainelEntrada.Visibility = Visibility.Visible;
        TituloBarra.Text = "";
        _ = VerificarServidorAsync();
    }

    private void AoPerderConexao()
    {
        if (PainelSala.Visibility != Visibility.Visible) return;
        MostrarAviso("A conexão com o servidor caiu.");
        SairDaSala();
    }

    private void DesmontarSala()
    {
        PararTransmissao();
        PararExibicao();

        _microfone?.Dispose(); _microfone = null;
        _somDoSistema?.Dispose(); _somDoSistema = null;
        _reproducao?.Dispose(); _reproducao = null;
        _transmissor?.Dispose(); _transmissor = null;
        _sinal?.Dispose(); _sinal = null;

        _participantes.Clear();
        _linkAtual = "";
        _idTransmissorAtual = 0;
    }

    // ================================================================== avisos e janela

    private void AtualizarSelo(string texto) => TextoSelo.Text = texto;

    private void MostrarAviso(string texto)
    {
        Registro.Escrever("aviso na tela: " + texto);
        TextoAviso.Text = texto;
        Aviso.Visibility = Visibility.Visible;

        _tempoAviso?.Stop();
        _tempoAviso = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _tempoAviso.Tick += (_, _) =>
        {
            _tempoAviso?.Stop();
            Aviso.Visibility = Visibility.Collapsed;
        };
        _tempoAviso.Start();
    }

    private void BarraTitulo_Arrastar(object remetente, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Janela_Minimizar(object remetente, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Janela_Maximizar(object remetente, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;

    private void Janela_Fechar(object remetente, RoutedEventArgs e) => Close();

    private void AoFechar(object? remetente, CancelEventArgs e)
    {
        _ajustes.Salvar();
        DesmontarSala();
    }
}
