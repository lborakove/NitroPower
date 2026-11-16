# Atualizar a BIOS pelo Linux

Como atualizar a BIOS do Acer Nitro ANV15-52 (Nitro V 15) sem Windows, com o
`fwupd`. Testado em 2026-11-15, da V1.31 para a V1.60, no Ubuntu 26.04 com
fwupd 2.1.1.

A Acer não publica a BIOS deste modelo no LVFS, por isso o
`fwupdmgr get-updates` não oferece nada. A atualização chega aos usuários de
Windows pelo Windows Update, e o mesmo pacote está no Microsoft Update Catalog.
O pacote traz uma cápsula UEFI assinada, que o `fwupd` consegue entregar ao
firmware.

> Gravar a BIOS tem risco. Uma queda de energia durante a gravação pode
> inutilizar a placa-mãe. Siga os passos com o carregador ligado e por sua
> conta.

## Antes de começar

- **Energia:** ligue o carregador e deixe a bateria carregada. O `fwupd` não
  prepara a atualização se o notebook estiver na bateria.
- **Disco criptografado:** com LUKS desbloqueado por senha, nada muda. Se o
  disco desbloqueia pelo TPM, tenha a chave de recuperação à mão: a BIOS nova
  altera as medições do TPM (PCR 0).
- **Secure Boot:** pode estar ligado ou desligado. O firmware confere a
  assinatura da cápsula nos dois casos.
- **Sem volta:** depois de gravada, a versão nova vira a mínima aceita. Não dá
  para voltar à anterior.

## 1. Ver a versão atual e o ID do firmware

```bash
cat /sys/class/dmi/id/bios_version
fwupdmgr get-devices | sed -n '/System Firmware/,/Device Flags/p' | grep -E 'Device ID|Current version|GUID'
```

Anote:

- **GUID:** no ANV15-52 é `12b99262-648c-4365-bafd-eb869fb7171e`. Ele aponta o
  pacote certo no catálogo e a imagem certa dentro do pacote.
- **Device ID:** o identificador do firmware no `fwupd`, uma sequência de 40
  caracteres hexadecimais. É usado no passo 4.
- **Current version:** um número decimal. Em hexadecimal, os quatro últimos
  dígitos são a versão: `1415721265` = `0x54623131` = V1.31, e
  `1415721312` = `0x54623160` = V1.60. Para converter:
  `printf '0x%08X\n' 1415721312`.

## 2. Baixar o pacote

1. Abra o catálogo com a busca pelo GUID:
   <https://www.catalog.update.microsoft.com/Search.aspx?q=12b99262-648c-4365-bafd-eb869fb7171e>
2. Escolha a linha **"Insyde Software Firmware Driver Update (1.0.XX.0)"** com
   a versão mais alta. A versão 1.0.60.0 corresponde à BIOS V1.60.
3. Clique em **Download** e salve o `.cab`, de uns 16 MB.

Não use o arquivo da página de suporte da Acer se ele tiver poucos KB: não é a
BIOS completa.

## 3. Conferir e extrair

```bash
sha1sum ~/Downloads/<arquivo>.cab
mkdir -p ~/bios && cabextract -d ~/bios ~/Downloads/<arquivo>.cab
grep -i DriverVer ~/bios/*.inf
grep -A3 '^\[Firmware_AddReg_' ~/bios/*.inf
```

- O SHA-1 tem de ser igual ao hash no fim do nome do arquivo baixado.
- O `cabextract` pode avisar de "extra bytes at end of file": é a assinatura
  do pacote, e o aviso pode ser ignorado.
- O pacote traz duas imagens `.fd`, uma para cada variante da placa (`ZYP` e
  `ZYQ`). O `.inf` diz qual é qual. Use **somente** a imagem cujo `FirmwareId`
  termina com o GUID do passo 1 (`…7171E`). Na V1.60:

  ```text
  [Firmware_AddReg_ZYP]
  HKR,,FirmwareId,,{12B99262-648C-4365-BAFD-EB869FB7171E}
  HKR,,FirmwareVersion,%REG_DWORD%,0x54623160
  HKR,,FirmwareCapsuleFilename,,"%13%\BIOS_V1.60_ZYP.fd"
  ```

  Aqui a imagem certa é a `BIOS_V1.60_ZYP.fd`. O `FirmwareVersion` dela
  (`0x54623160`) precisa ser maior que a versão atual do passo 1.

## 4. Preparar a gravação

```bash
sudo systemctl stop fwupd
sudo fwupdtool install-blob ~/bios/BIOS_VX.XX_ZYP.fd <Device ID>
sudo systemctl start fwupd
```

- O pacote do catálogo é do Windows Update, não do LVFS. Por isso o
  `fwupdmgr install` não o aceita, e é preciso entregar a imagem direto ao
  dispositivo com o `fwupdtool install-blob`.
- O `fwupdtool` trabalha sem o serviço do `fwupd`, por isso o serviço é
  parado antes e religado depois.
- Este passo não grava nada. Ele só deixa a cápsula pronta para o próximo
  boot.
- **Se aparecer erro ou aviso em vez de sucesso, não reinicie.** Investigue
  antes.

## 5. Reiniciar e gravar

```bash
sudo reboot
```

- Aparece uma tela da Insyde com uma barra de progresso. Ali o firmware confere
  a assinatura e recusa uma imagem de outro modelo.
- O notebook pode reiniciar mais de uma vez, e as ventoinhas podem ir ao
  máximo. Leva alguns minutos.
- **Não desligue nem tire da tomada** até o sistema voltar.

## 6. Se o Ubuntu não iniciar

A atualização pode apagar as entradas de boot. Aperte **F12** na tela da Acer,
escolha `ubuntu` e, já no sistema, recrie a entrada:

```bash
lsblk -o NAME,FSTYPE,MOUNTPOINT | grep -E 'vfat|/boot/efi'
sudo efibootmgr -c -d /dev/nvme0n1 -p 1 -L ubuntu -l '\EFI\ubuntu\shimx64.efi'
```

O exemplo supõe a partição EFI em `nvme0n1p1`. Ajuste `-d` e `-p` ao que o
`lsblk` mostrar.

## 7. Conferir depois

```bash
cat /sys/class/dmi/id/bios_version
fwupdmgr get-devices | sed -n '/System Firmware/,/Device Flags/p' | grep -E 'Current version|Update State'
cat /sys/firmware/acpi/platform_profile_choices
journalctl -u nitropowerd -b --no-pager | head -12
```

- A versão nova deve aparecer, com `Update State: Success`.
- Teste os modos, o turbo, as ventoinhas e a tecla Nitro. Uma BIOS nova pode
  mudar o caminho que o driver usa para trocar de modo (veja
  [Linuwu-Sense no NitroPower](LINUWU-SENSE.md)).
- Se tudo funcionar, acrescente a versão em `testedBios` no perfil
  [`profiles/acer-nitro-anv15-52.json`](../profiles/acer-nitro-anv15-52.json).
  Se algo parar de funcionar, abra uma issue com a versão da BIOS e a saída
  dos comandos acima.

## Versões conhecidas do ANV15-52

| BIOS | Situação no NitroPower |
| --- | --- |
| V1.31 | testada |
| V1.52 | não testada |
| V1.53 | não testada. Segundo o Linuwu-Sense upstream, troca de modo pelo `EC0._Q30`, que a V1.31 e a V1.60 não têm |
| V1.60 | testada, atualizada por este roteiro. Traz os certificados de Secure Boot de 2023 da Microsoft |
