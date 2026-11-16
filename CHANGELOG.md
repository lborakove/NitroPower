# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/);
versões seguem [SemVer](https://semver.org/lang/pt-BR/).

## [Não lançado]

### Adicionado

- Estrutura do repositório e arquitetura.
- Perfil do Acer Nitro V 15 (ANV15-52), BIOS V1.31/V1.60.
- Guia para atualizar a BIOS pelo Linux com o fwupd (`docs/BIOS.md`).
- Driver `linuwu_sense` (upstream 4483b6c) com o patch 0001: fallback para a
  interface WMI quando `EC0._Q30` não existe, e o modo Performance da Acer
  exposto como `performance` quando o firmware não tem turbo.
- `NitroPower.Core` (.NET 10): perfis de modelo com validação e identificação
  pela DMI; `platform_profile`; recursos do `nitro_sense` (ventoinhas, bateria,
  carga USB, luz do teclado, LCD overdrive, som de boot); energia e PCI; NVML
  (offsets de clock e telemetria, sem acordar a GPU suspensa); sensores (EC da
  Acer, coretemp, NVMe, uso e frequência da CPU); política de overclock com
  histerese de bateria; curva de ventoinha com controlador com histerese.
- `nitropower-cli probe`: diagnóstico de hardware só de leitura.
- Paridade com o Linuwu-Sense ([docs/LINUWU-SENSE.md](docs/LINUWU-SENSE.md)):
  - turbo (modo máximo + ventoinhas no máximo + overclock do modo), com
    restauração do modo e das ventoinhas anteriores;
  - tecla de modo em espaço de usuário (ciclo na tomada e na bateria, quiet e
    eco devolvem as ventoinhas ao automático) e o parâmetro
    `cycle_gaming_thermal_profile`;
  - teclado RGB de 4 zonas: 8 efeitos e cor por zona, com as regras de
    validação do driver; `turbo_sense/turbo_state`;
  - presets nomeados (`/etc/nitropower/presets`) e estado persistente
    (`/var/lib/nitropower/state.json`);
  - `watch --notify`: avisos de modo e turbo, com `poll()` no sysfs.
- `nitropower-cli`: comandos para todas as funções (`status`, `mode`, `turbo`,
  `fan`, `gpu-oc`, `mode-key`, `battery-limit`, `battery-calibration`,
  `usb-charging`, `keyboard`, `keyboard-timeout`, `lcd-overdrive`,
  `boot-sound`, `preset`, `watch`). Perfis copiados ao lado do executável.
- Perfil: `modeCycle` e `turbo` (opcionais) e o ANV15-52 preenchido.
- Driver, patch 0002: parâmetros `turbo_sense` e `four_zone_kb` para ligar
  esses recursos em modelos fora da tabela de quirks.
- `nitropowerd` (systemd, `Type=notify`): API JSON-RPC 2.0 em socket Unix
  (`root:nitropower` 0660, eventos), tecla Nitro pelo evdev (ação `turbo`,
  `cycle`, `gui` ou `none`), overclock da GPU no lugar do `nitro-gpu-oc` (com
  histerese de bateria persistente e reaplicação após reset), curva de
  ventoinha por software, fim do turbo quando outro programa troca o modo.
- `INitroApi` com execução local e cliente (`NitroClient`); a CLI usa o daemon
  quando ele roda (sem sudo para o grupo `nitropower`) e cai para o acesso
  direto. Novos comandos: `fan curve`, `nitro-key`, `--direct`.
- Ventoinhas com escolha persistente (`auto`, `max`, manual, `curve`); o turbo
  restaura a curva. Presets guardam a curva.
- `watch --notify` usa os eventos do daemon e avisa falhas da tecla Nitro.
- `packaging/`: `build.sh`, `install-dev.sh` (instala e desfaz; desativa o
  `nitro-gpu-oc` e o atalho do GNOME), unidade systemd e autostart dos avisos.
- Interface gráfica `nitropower` (Avalonia 12, fase 3), cliente do daemon:
  - Painel com medidores e gráficos de 2 minutos (temperaturas, ventoinhas,
    uso), controle rápido de modo, turbo e ventoinhas;
  - Desempenho: modos, turbo, ciclo da tecla de modo, overclock da GPU com
    níveis editáveis por modo;
  - Ventoinhas: automático, máximo, manual e editor gráfico de curva (CPU e
    GPU, modelos prontos, marcador da temperatura atual);
  - Bateria (carga, potência, tempo, saúde, ciclos, limite, calibração, carga
    USB), Teclado e tela (luz, overdrive, som, RGB de 4 zonas com prévia),
    Tecla Nitro, Perfis, Sistema (driver, parâmetros, registros do serviço);
  - bandeja do sistema, uma instância por usuário (`show` pelo socket),
    `--hidden`, `--page`, `--demo`; português e inglês; tema escuro e claro;
    início com a sessão; a ação `gui` da tecla Nitro abre a janela.
- API: `gpu.levels.set` e `gpu.levels.reset` (níveis de overclock do usuário,
  guardados no estado); `status.get` traz `modeCycle`, `gpuOcLevel` de cada
  modo e os níveis e a política do overclock. CLI: `gpu-oc levels`.
- `tools/NitroPower.Gui.Shots`: capturas das páginas sem monitor (headless).
- `install-dev.sh` instala a interface, o atalho e os ícones; `--replace-damx`
  esconde também o atalho do DAMX. `watch --notify` abre a janela pela tecla
  Nitro quando ela não está aberta.
- 144 testes: 127 do Core e 17 da interface (textos nos dois idiomas, bateria,
  e testes de interação headless: modos, turbo, curva, perfis, idioma).

### Segurança

- `install-dev.sh` instalava os arquivos do daemon com o dono do build
  (`cp -a`): o usuário podia trocar código que roda como root. Agora são de
  root, sem escrita para grupo e outros.
- API: requisição limitada a 64 KiB (antes uma linha sem fim enchia a memória
  do daemon), no máximo 32 conexões, limite de 100 presets.
- `gpu.offsets.set` saiu da API (faixa larga e desfeito pelo daemon em
  segundos); `gpu-oc <core> <mem>` só como root sem o daemon.
- Tecla Nitro: filtro `EVIOCSMASK`, o daemon não recebe as outras teclas.
- Journal: mensagens com quebra de linha não forjam outras entradas.
- systemd: `PrivateNetwork`, `RestrictAddressFamilies`, `ProtectKernelModules`,
  `ProtectKernelLogs`, `ProtectControlGroups`, `ProtectHostname`,
  `ProtectProc`, `RestrictNamespaces`, `RestrictRealtime`, `RestrictSUIDSGID`,
  `LockPersonality`, `SystemCallArchitectures` (exposição 8,7 → 5,4).
- `--replace-damx` remove a escrita direta do grupo `linuwu_sense` no sysfs.
- Interface: socket de instância única fora do `/tmp` compartilhado;
  `notify-send` recebe o texto depois de `--`.

### Corrigido

- `driver/linuwu_sense/dkms-sign.sh` faltava (o `dkms.conf` o chama).
- Perfil apontava para um `profile.schema.json` inexistente.
- Removidos membros sem uso e comentários divisores.
