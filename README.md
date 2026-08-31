# Transcriber

O Transcriber é um aplicativo desktop de baixa latência para Windows que captura áudio de uma saída virtual do Voicemeeter e transcreve a fala localmente com o Whisper.

O design atual destina-se à transcrição quase em tempo real, mantendo o fluxo de captura independente da thread da interface e impedindo que o processamento do Whisper acumule áudio desatualizado.

## Configuração recomendada

A configuração abaixo apresentou o melhor resultado prático até o momento:

### Voicemeeter

- **Buffer WDM: 416 amostras** — essa configuração foi testada e considerada **ótima** para o ambiente atual.
- Direcione o áudio a ser transcrito para a saída virtual **B / B1 Virtual Out** do Voicemeeter.
- O fluxo físico de reprodução pode continuar por **A / A1** até o headset.

Fluxo típico:

```text
Áudio do Teams / Meet / Zoom / Windows
                |
                v
        Entrada do Voicemeeter
                |
          +-----+-----+
          |           |
          A           B / B1
          |           |
          v           v
     A1 / Headset   Saída virtual
                      |
                      v
                  Transcriber
```

### Transcriber

Configuração recomendada nos menus suspensos:

| Configuração | Valor recomendado |
|---|---:|
| Contexto | **1,0 s** |
| Atualização | **0,50 s** |
| VAD | **Ativado** |
| Silêncio | **500 ms** |

Esses valores oferecem atualmente um bom equilíbrio entre latência da transcrição, preservação de palavras e separação natural entre blocos de fala.

## Endpoint de captura

O Transcriber enumera os **endpoints de captura** do Windows usando:

```csharp
DataFlow.Capture
```

e captura o endpoint selecionado usando:

```csharp
WasapiCapture
```

Isso é intencional. A saída B/B1 do Voicemeeter é exposta aos aplicativos como um endpoint de gravação/captura.

Em instalações padrão, o dispositivo esperado normalmente tem um nome semelhante a:

```text
Voicemeeter Output (VB-Audio Voicemeeter VAIO)
```

Algumas instalações podem apresentá-lo como:

```text
Voicemeeter Out B1
```

Não selecione `Voicemeeter Input` para a transcrição da B1. `Input` representa o lado de reprodução que entra no Voicemeeter, enquanto `Output` / `Out B1` representa a saída virtual de gravação consumida pelo Transcriber.

## Arquitetura de áudio

O fluxo de processamento atual é:

```text
Voicemeeter B / B1
        |
        v
WasapiCapture
        |
        v
Conversão PCM / Float
        |
        v
Áudio mono
        |
        v
Reamostragem para 16 kHz
        |
        v
VAD adaptativo
        |
        +----------------------+
        |                      |
        v                      v
Contexto deslizante      Buffer da fala completa
(baixa latência)         (recuperação de precisão)
        |                      |
        v                      |
Fila somente do mais recente  |
        |                      |
        v                      |
Parciais do Whisper            |
        |                      |
        +----------+-----------+
                   |
              silêncio detectado
                   |
                   v
          passagem final do Whisper
                   |
                   v
        transcrição consolidada
```

## Transcrição deslizante de baixa latência

Versões anteriores aguardavam uma janela fixa de áudio relativamente grande antes de acionar o Whisper. A versão atual separa **Contexto** de **Atualização**.

Com a configuração recomendada:

```text
Contexto = 1,0 s
Atualização = 0,50 s
```

O Transcriber mantém aproximadamente um segundo do contexto recente da fala e tenta atualizar a transcrição provisória a cada meio segundo durante a fala ativa. Isso proporciona retorno visual mais rápido sem obrigar o aplicativo a aguardar uma frase longa inteira.

## Fila do Whisper somente com o item mais recente

O canal de processamento do Whisper comporta um trabalho pendente e usa:

```csharp
BoundedChannelFullMode.DropOldest
```

Se a inferência demorar mais do que a produção de novos trabalhos parciais, os trabalhos pendentes desatualizados serão descartados. O objetivo é manter a transcrição exibida próxima do áudio atual, sem criar uma fila crescente de processamento atrasado.

## VAD

O Transcriber usa um detector adaptativo de atividade de voz baseado em RMS. O VAD:

- estima o nível de ruído de fundo;
- detecta o início da fala;
- mantém um breve pré-buffer para reduzir a possibilidade de cortar o início de uma palavra;
- detecta silêncio contínuo;
- finaliza uma fala após o intervalo de silêncio configurado.

A configuração recomendada é:

```text
VAD = Ativado
Silêncio = 500 ms
```

Um silêncio menor que o intervalo configurado continua fazendo parte da fala atual. Um silêncio igual ou maior finaliza a fala atual.

## Silêncio e novas linhas

Um período finalizado de silêncio representa o limite entre blocos de fala. Quando o silêncio atinge o limite selecionado:

1. a fala atual é finalizada;
2. uma passagem final do Whisper é agendada;
3. o texto finalizado é consolidado;
4. a próxima fala começa em uma **nova linha**.

Dentro da mesma fala, as atualizações parciais permanecem contínuas, em vez de criar uma nova linha para cada resultado.

## Recuperação de palavras perdidas pela transcrição parcial

Janelas deslizantes muito curtas melhoram a latência, mas podem omitir ou revisar palavras. Para compensar, o Transcriber mantém dois buffers:

### Buffer deslizante

Usado para transcrição provisória rápida enquanto a pessoa fala.

### Buffer da fala completa

Mantém toda a fala atual, em vez de reduzi-la continuamente ao contexto curto. Ao detectar silêncio, o Transcriber faz uma transcrição final com esse buffer. O resultado final tem preferência sobre a hipótese provisória acumulada e busca recuperar palavras perdidas nas janelas curtas.

O processamento final é limitado para evitar uma solicitação excessivamente custosa em falas ininterruptas muito longas.

## Refinamento de texto de legendas ao vivo

Os resultados parciais são tratados como **hipóteses provisórias**, não como frases finais independentes. Por exemplo:

```text
Diga-nos por que
Diga-nos por que você é
você é uma boa opção
uma boa opção para esta vaga.
```

Esses resultados devem refinar progressivamente a mesma fala, em vez de serem simplesmente acrescentados. O aplicativo mantém:

```csharp
_committedText
_currentUtteranceText
```

Assim, a fala atual pode ser substituída ou refinada à medida que novas hipóteses chegam. Quando o silêncio finaliza a fala, a transcrição final é consolidada.

## Formatação de texto contínuo

Os registros de data e hora foram removidos da transcrição visível. Fragmentos parciais da mesma fala são exibidos como texto contínuo. O espaçamento considera a pontuação, e novas linhas são reservadas aos limites de fala detectados pelo intervalo de silêncio.

## Botão Limpar

O botão **Limpar** apaga:

- a transcrição visível;
- o estado interno do texto consolidado/provisório.

Limpar apenas o RichTextBox permitiria que o texto interno reaparecesse em uma atualização posterior; por isso, ambos os estados são redefinidos juntos.

## Configurações persistentes

As configurações mais recentes são salvas automaticamente e restauradas na próxima execução. Isso inclui:

- Dispositivo de captura
- Contexto
- Atualização
- VAD
- Silêncio

O arquivo JSON é armazenado em:

```text
%LOCALAPPDATA%\InterviewTranscriber\settings.json
```

O Local AppData evita a necessidade de permissão de gravação no diretório de instalação. Se o arquivo estiver ausente ou for inválido, o Transcriber usará os padrões.

## Segurança entre threads

Os controles WPF pertencem à thread da interface. Callbacks de áudio e o Whisper são executados em threads de trabalho. Portanto:

- os controles são lidos quando a configuração é capturada antes do início;
- as threads de trabalho usam campos comuns, sem ler ComboBoxes diretamente;
- objetos `MMDevice` / WASAPI são criados para a captura sem compartilhar objetos da interface;
- atualizações da interface usam `Dispatcher.BeginInvoke`.

Isso evita erros como:

```text
O thread de chamada não pode acessar este objeto porque ele pertence a um thread diferente
```

## Manipulação do WAV em memória

O Whisper recebe um fluxo WAV em memória. O `WaveFileWriter` normalmente descarta seu fluxo subjacente ao ser descartado. O Transcriber envolve o `MemoryStream` com:

```csharp
IgnoreDisposeStream
```

Isso permite finalizar os dados WAV sem fechar o `MemoryStream` antes da leitura pelo Whisper e corrige:

```text
Cannot access a closed Stream
```

A posição do fluxo é redefinida para zero antes de ele ser passado ao Whisper.

## Whisper

O Whisper é executado localmente por meio de:

- `Whisper.net`
- `Whisper.net.AllRuntimes`

Não é necessário um `whisper.exe` separado. O modelo deve estar em:

```text
<diretório do executável do Transcriber>\Models\ggml-base.bin
```

O modelo recomendado é `ggml-base.bin`. Modelos menores podem reduzir o tempo de inferência em detrimento da precisão.

## Formatos de áudio

O fluxo de captura aceita:

- IEEE Float, 32 bits
- PCM, 16 bits

O áudio é convertido para mono e reamostrado para:

```text
16.000 Hz
```

antes de ser preparado para o Whisper.

## Resumo da configuração recomendada atual

```text
Voicemeeter
  Buffer WDM: 416 amostras

Transcriber
  Captura: Voicemeeter Output / Out B1
  Contexto: 1,0 s
  Atualização: 0,50 s
  VAD: Ativado
  Silêncio: 500 ms

Whisper
  Modelo: ggml-base.bin
```

Esta é a configuração de referência atual para as próximas versões do Transcriber.
