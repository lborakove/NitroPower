# Updating the BIOS from Linux

How to update the BIOS of the Acer Nitro ANV15-52 (Nitro V 15) without
Windows, using `fwupd`. Tested on 2026-11-15, from V1.31 to V1.60, on Ubuntu
26.04 with fwupd 2.1.1.

Acer does not publish this model's BIOS on the LVFS, so
`fwupdmgr get-updates` offers nothing. Windows users get the update through
Windows Update, and the same package is in the Microsoft Update Catalog. It
contains a signed UEFI capsule that `fwupd` can hand to the firmware.

> Flashing the BIOS carries risk. A power loss while it is being written can
> brick the motherboard. Follow these steps with the charger plugged in, at
> your own risk.

## Before you start

- **Power:** plug in the charger and keep the battery charged. `fwupd` will
  not schedule the update while the laptop runs on battery.
- **Encrypted disk:** with LUKS unlocked by a passphrase, nothing changes. If
  the disk unlocks through the TPM, keep the recovery key at hand: the new BIOS
  changes the TPM measurements (PCR 0).
- **Secure Boot:** it can be on or off. The firmware checks the capsule's
  signature either way.
- **No going back:** once flashed, the new version becomes the minimum
  accepted. You cannot return to the previous one.

## 1. Check the current version and the firmware ID

```bash
cat /sys/class/dmi/id/bios_version
fwupdmgr get-devices | sed -n '/System Firmware/,/Device Flags/p' | grep -E 'Device ID|Current version|GUID'
```

Write down:

- **GUID:** on the ANV15-52 it is `12b99262-648c-4365-bafd-eb869fb7171e`. It
  identifies the right package in the catalog and the right image inside the
  package.
- **Device ID:** the firmware's identifier in `fwupd`, a string of 40
  hexadecimal characters. It is used in step 4.
- **Current version:** a decimal number. In hexadecimal, its last four digits
  are the version: `1415721265` = `0x54623131` = V1.31, and
  `1415721312` = `0x54623160` = V1.60. To convert:
  `printf '0x%08X\n' 1415721312`.

## 2. Download the package

1. Open the catalog searching for the GUID:
   <https://www.catalog.update.microsoft.com/Search.aspx?q=12b99262-648c-4365-bafd-eb869fb7171e>
2. Pick the **"Insyde Software Firmware Driver Update (1.0.XX.0)"** row with
   the highest version. Version 1.0.60.0 is BIOS V1.60.
3. Click **Download** and save the `.cab`, about 16 MB.

Do not use the file from Acer's support page if it is only a few KB: it is not
the full BIOS.

## 3. Verify and extract

```bash
sha1sum ~/Downloads/<file>.cab
mkdir -p ~/bios && cabextract -d ~/bios ~/Downloads/<file>.cab
grep -i DriverVer ~/bios/*.inf
grep -A3 '^\[Firmware_AddReg_' ~/bios/*.inf
```

- The SHA-1 must match the hash at the end of the downloaded file's name.
- `cabextract` may warn about "extra bytes at end of file": that is the
  package's signature, and the warning can be ignored.
- The package contains two `.fd` images, one for each board variant (`ZYP` and
  `ZYQ`). The `.inf` says which is which. Use **only** the image whose
  `FirmwareId` ends with the GUID from step 1 (`…7171E`). For V1.60:

  ```text
  [Firmware_AddReg_ZYP]
  HKR,,FirmwareId,,{12B99262-648C-4365-BAFD-EB869FB7171E}
  HKR,,FirmwareVersion,%REG_DWORD%,0x54623160
  HKR,,FirmwareCapsuleFilename,,"%13%\BIOS_V1.60_ZYP.fd"
  ```

  Here the right image is `BIOS_V1.60_ZYP.fd`. Its `FirmwareVersion`
  (`0x54623160`) must be higher than the current version from step 1.

## 4. Stage the update

```bash
sudo systemctl stop fwupd
sudo fwupdtool install-blob ~/bios/BIOS_VX.XX_ZYP.fd <Device ID>
sudo systemctl start fwupd
```

- The catalog package comes from Windows Update, not from the LVFS. That is
  why `fwupdmgr install` does not accept it, and the image has to be handed
  straight to the device with `fwupdtool install-blob`.
- `fwupdtool` works without the `fwupd` service, so the service is stopped
  before and started again after.
- This step writes nothing. It only stages the capsule for the next boot.
- **If you get an error or a warning instead of success, do not reboot.**
  Investigate first.

## 5. Reboot and flash

```bash
sudo reboot
```

- An Insyde screen with a progress bar appears. There the firmware checks the
  signature and rejects an image for another model.
- The laptop may restart more than once, and the fans may spin up to maximum.
  It takes a few minutes.
- **Do not power off or unplug** until the system is back.

## 6. If Ubuntu does not boot

The update may erase the boot entries. Press **F12** at the Acer screen, choose
`ubuntu` and, once in the system, recreate the entry:

```bash
lsblk -o NAME,FSTYPE,MOUNTPOINT | grep -E 'vfat|/boot/efi'
sudo efibootmgr -c -d /dev/nvme0n1 -p 1 -L ubuntu -l '\EFI\ubuntu\shimx64.efi'
```

The example assumes the EFI partition is `nvme0n1p1`. Adjust `-d` and `-p` to
what `lsblk` shows.

## 7. Check afterwards

```bash
cat /sys/class/dmi/id/bios_version
fwupdmgr get-devices | sed -n '/System Firmware/,/Device Flags/p' | grep -E 'Current version|Update State'
cat /sys/firmware/acpi/platform_profile_choices
journalctl -u nitropowerd -b --no-pager | head -12
```

- The new version should show, with `Update State: Success`.
- Test the modes, turbo, fans and the Nitro key. A new BIOS may change the path
  the driver uses to switch modes (see
  [Linuwu-Sense in NitroPower](LINUWU-SENSE.md)).
- If everything works, add the version to `testedBios` in the profile
  [`profiles/acer-nitro-anv15-52.json`](../profiles/acer-nitro-anv15-52.json).
  If something stops working, open an issue with the BIOS version and the
  output of the commands above.

## Known ANV15-52 versions

| BIOS | Status in NitroPower |
| --- | --- |
| V1.31 | tested |
| V1.52 | untested |
| V1.53 | untested. According to upstream Linuwu-Sense, it switches modes through `EC0._Q30`, which V1.31 and V1.60 lack |
| V1.60 | tested, updated with this guide. Includes Microsoft's 2023 Secure Boot certificates |
