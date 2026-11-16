# Arquitetura do NitroPower

Um único pacote `.deb` que substitui o DAMX, o `nitro-gpu-oc` e o atalho do
botão Nitro. Licença: GPL-3.0-or-later. O driver incluído é GPL-2.0-or-later.

## Componentes

```
nitropower (.deb)
├── driver/linuwu_sense      módulo do kernel (DKMS) + patches próprios
├── nitropowerd              daemon root (systemd): a única fonte de verdade
├── nitropower               interface gráfica (Avalonia)
├── nitropower-cli           linha de comando
└── /usr/share/nitropower/profiles/*.json   perfis de modelo
```

### Projetos .NET (`src/`)

| Projeto | Papel |
|---|---|
| `NitroPower.Core` | Perfis de modelo, acesso a sysfs/hwmon, NVML, políticas e `Control/NitroController`, que executa cada função (o mesmo código para CLI, daemon e interface) |
| `NitroPower.Daemon` | Serviço root: aplica configurações, política de overclock, curva de ventoinha, botão Nitro, estado persistente |
| `NitroPower.Cli` | Linha de comando: cliente da API quando o daemon roda; sem ele, executa direto (como root) |
| `NitroPower.Gui` | Interface Avalonia 12 (MVVM com CommunityToolkit.Mvvm), executável `nitropower` |
| `tests/NitroPower.Core.Tests` | Testes de unidade (perfis, protocolo, política) |
| `tests/NitroPower.Gui.Tests` | Textos, leitores locais e testes de interação headless |
| `tools/NitroPower.Gui.Shots` | Capturas das páginas (modo demo, sem monitor) |

### Camada de controle (`NitroPower.Core.Control`)

- `NitroController`: modos, tecla de modo, turbo, ventoinhas, bateria, carga
  USB, teclado (timeout e RGB), LCD overdrive, som de boot, overclock da GPU e
  o parâmetro da tecla de modo do driver.
- `StateStore`: `/var/lib/nitropower/state.json` (turbo e o que restaurar,
  último modo fora do turbo, overclock desligado). Grava antes de mexer no
  hardware e desfaz se o driver recusar.
- `Presets`: presets nomeados em `/etc/nitropower/presets`.
- Turbo segue o Linuwu-Sense: modo mais alto + ventoinhas do perfil (`turbo`
  no JSON); o overclock da GPU vem do nível desse modo.

Equivalência completa com o Linuwu-Sense: [LINUWU-SENSE.md](LINUWU-SENSE.md).

### Daemon (`nitropowerd`)

Roda como root (systemd, `Type=notify`) e é o único que escreve no hardware
enquanto está ativo. Todo acesso ao hardware passa por um único lock.

| Linha de execução | Função |
|---|---|
| API | socket Unix, uma tarefa por cliente ([API.md](API.md)) |
| Vigia | `poll()` no `platform_profile` (e `turbo_state`) com espera de 5 s: avisa mudança de modo, encerra o turbo se outro programa trocou o modo (e devolve as ventoinhas), detecta tomada/bateria e mantém o overclock da GPU (substitui o `nitro-gpu-oc`: não acorda a GPU, reaplica após reset) |
| Ventoinhas | curva por software a cada 2 s quando a escolha é `curve`; a ventoinha da CPU segue a CPU (coretemp) e a da GPU segue a GPU (EC); só escreve quando muda |
| Tecla Nitro | lê `KEY_PROG1` do evdev dos dispositivos internos do perfil, sem capturar o teclado; executa a ação configurada (`turbo`, `cycle`, `gui`, `none`) |

### Interface (`nitropower`)

- `Services/NitroSession`: conexão com o daemon. Chamadas em threads de
  trabalho, uma por vez (o `NitroClient` não é thread-safe); eventos numa
  segunda conexão; reconecta a cada 3 s; lê sensores só com a janela visível.
  `DemoNitroApi` imita um Nitro com RGB para `--demo`, testes e capturas.
- `ViewModels/`: uma por página. O status chega com `Syncing` ligado, para os
  setters não devolverem o valor ao daemon; edições em andamento (curva,
  níveis de overclock) só são recarregadas quando o valor salvo muda.
- `Controls/`: desenhados à mão: `ArcGauge`, `HistoryChart`, `FanCurveEditor`
  (arrastar mantém a curva válida: temperatura crescente, potência nunca cai),
  `KeyboardPreview`, `Icon` (ícones de linha próprios em `Styles/Icons.axaml`).
- `Localization/`: `Strings` (português e inglês) e `{l:Tr chave}` no XAML,
  que troca o idioma com a janela aberta.
- Bandeja (StatusNotifierItem), uma instância por usuário
  (`$XDG_RUNTIME_DIR/nitropower-gui-$USER.sock`), preferências em
  `~/.config/nitropower/gui.json`, início com a sessão em
  `~/.config/autostart/nitropower.desktop`.

### API do daemon

- Socket Unix em `/run/nitropower/nitropower.sock`, de propriedade
  `root:nitropower`, modo 0660. Só membros do grupo `nitropower` controlam o
  hardware, sem sudo.
- JSON-RPC 2.0, uma mensagem por linha; `system.version` informa a versão do
  protocolo. Eventos para os clientes: modo, energia, tecla Nitro, mudanças.
- `INitroApi` é a mesma interface para o cliente (socket) e para a execução
  local, então CLI e interface gráfica não sabem a diferença.
- Referência: [API.md](API.md).

### Perfis de modelo

Cada modelo é um JSON em `profiles/`, identificado pela DMI e pelas IDs de PCI
da GPU. O perfil declara modos e valores do firmware, níveis de overclock por
GPU, recursos disponíveis e o código da tecla Nitro. Para suportar um modelo
novo basta acrescentar um arquivo, sem mudar o código. Recurso marcado
`"unknown"` é sondado em tempo de execução. Campos opcionais: `modeCycle`
(ordem da tecla de modo na tomada e na bateria, e quais modos devolvem as
ventoinhas ao automático) e `turbo` (modo e ventoinhas do turbo).

## Segurança

- **Quem controla o hardware.** Root e os membros do grupo `nitropower`, pelo
  socket `0660`. Estar no grupo equivale a administrar o hardware: trocar
  modos, ventoinhas (até 1%) e overclock dentro dos limites do perfil. Não dá
  acesso a outros arquivos nem a comandos do sistema.
- **Entrada da API.** Cada método valida os parâmetros (modos e recursos do
  perfil, curva segura, níveis de overclock em núcleo -300..300 e memória
  -1000..1500 MHz, nomes de perfil sem `/`). Requisições têm no máximo 64 KiB,
  o servidor aceita até 32 conexões e cada cliente tem 2 s para ler um evento.
  Há no máximo 100 presets. Offsets manuais da GPU não existem na API: só a
  CLI como root, sem o daemon.
- **Tecla Nitro.** O daemon abre o teclado interno pelo evdev, mas pede ao
  kernel (`EVIOCSMASK`) só a tecla Nitro: o resto do que é digitado não chega
  ao processo. Se o kernel não aceitar o filtro, o daemon registra isso.
- **Sandbox do systemd.** Sistema de arquivos só leitura (exceto estado,
  configuração e socket), sem rede (`PrivateNetwork`, só `AF_UNIX`/`AF_NETLINK`),
  sem carregar módulos, sem namespaces, sem SUID. `/sys` e `/dev` continuam
  graváveis porque são o trabalho do daemon.
- **Registros.** Cada mensagem vira uma linha no journal (quebras de linha são
  trocadas), para texto vindo de fora não forjar outras entradas.
- **Instalação.** Os arquivos em `/usr/lib/nitropower` são de root e não
  graváveis por grupo ou outros: o daemon roda como root a partir deles. O
  `--replace-damx` remove a regra do DAMX que dava ao grupo `linuwu_sense`
  escrita direta no sysfs, para o daemon ser o único a escrever.
- **Driver.** O código em `/usr/src/linuwu_sense-*` é compilado como root pelo
  DKMS a cada kernel novo: precisa ser de root (`chown -R root:root`).

## Versionamento

- SemVer: `MAJOR.MINOR.PATCH`. A versão fica em `Directory.Build.props` e em
  `debian/changelog`.
- Versões separadas para o protocolo da API e para o esquema dos perfis
  (`schemaVersion`).
- O driver registra a base upstream em `driver/UPSTREAM` e as mudanças como
  patches numerados em `driver/patches/`.

## Instalação e substituição do legado

No `postinst` do pacote:

1. Desativar e remover `damx-daemon`, `nitro-gpu-oc` e o lançador
   `nitro-key-detection`, se existirem.
2. Remover o atalho GNOME `Launch1` → `nitro-key-launch`. Agora o daemon cuida
   do botão Nitro.
3. Registrar o módulo no DKMS (`linuwu_sense`), compilar e rodar
   `update-initramfs -u`. O dracut copia o módulo para o initramfs.
4. Criar o grupo `nitropower`, adicionar o usuário e iniciar `nitropowerd`.

## Roteiro

| Fase | Entrega |
|---|---|
| 0 | Repositório, documentação, perfil ANV15-52, driver com patches |
| 1 | `NitroPower.Core`: detecção de modelo, leitura e escrita de recursos, NVML, monitoramento |
| 1.5 | Paridade com o Linuwu-Sense: turbo, tecla de modo, RGB de 4 zonas, presets, notificações; `nitropower-cli` executa tudo; patch 0002 |
| 2 | `nitropowerd` + `nitropower-cli`: API, política de overclock, curva de ventoinha, tecla Nitro, avisos; instalação de desenvolvimento (`packaging/`) |
| 3 | Interface Avalonia: painel, desempenho, ventoinhas, bateria, tela/teclado, tecla Nitro, perfis, sistema, bandeja (feito) |
| 4 | Pacote `.deb` com DKMS, systemd, polkit/grupo e substituição do legado |
| 5 | Sondagem dos recursos "a testar": curva no firmware, logo de boot, tecla Windows, RGB, MUX |
