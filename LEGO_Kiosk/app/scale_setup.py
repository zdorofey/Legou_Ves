from __future__ import annotations

import asyncio
import json
import os
import queue
import sys
import threading
import time
from dataclasses import dataclass, replace
from typing import Any

import pygame
from bleak import BleakClient, BleakScanner


CHAR_UUID = "00002a9d-0000-1000-8000-00805f9b34fb"
WEIGHT_SERVICE_UUID = "181d"
BODY_COMPOSITION_SERVICE_UUID = "181b"
WINDOW_SIZE = (1000, 700)

BG = (255, 248, 222)
PANEL = (255, 255, 255)
PANEL_ALT = (255, 239, 178)
INK = (29, 27, 22)
MUTED = (104, 97, 82)
RED = (205, 23, 27)
YELLOW = (255, 218, 65)
LINE = (235, 224, 189)


@dataclass(frozen=True, slots=True)
class ScanRecord:
    device: Any
    advertisement: Any
    scale_hint: str | None


def short_uuid(value: str) -> str:
    compact = str(value).lower().replace("-", "")
    if len(compact) >= 8 and compact.startswith("0000"):
        return compact[4:8]
    return compact


def record_name(record: ScanRecord) -> str:
    return record.device.name or record.advertisement.local_name or "BLE-устройство без имени"


def application_directory() -> str:
    if getattr(sys, "frozen", False):
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.abspath(__file__))


class ScaleSetup:
    def __init__(self) -> None:
        pygame.init()
        pygame.display.set_caption("Привязка весов · LEGO")
        self.screen = pygame.display.set_mode(WINDOW_SIZE, pygame.RESIZABLE)
        self.clock = pygame.time.Clock()
        self.font = pygame.font.SysFont("Segoe UI", 18)
        self.small_font = pygame.font.SysFont("Segoe UI", 14)
        self.title_font = pygame.font.SysFont("Segoe UI", 28, bold=True)
        self.number_font = pygame.font.SysFont("Segoe UI", 42, bold=True)

        self.events: queue.Queue[tuple[str, Any]] = queue.Queue()
        self.scan_records: list[ScanRecord] = []
        self.scanned_total = 0
        self.show_all_devices = False
        self.selected_index = -1
        self.list_offset = 0
        self.busy = False
        self.client: BleakClient | None = None
        self.last_notification_time: float | None = None
        self.packet_count = 0
        self.weight = "—"
        self.packet_line = "После подключения здесь появятся пакеты и интервалы между ними."
        self.status = "Найдём Xiaomi Mi Scale и BLE-весы с совместимой службой."
        self.connected_address: str | None = None
        self.saved_address = self._read_saved_address()

        self.loop = asyncio.new_event_loop()
        self.loop_thread = threading.Thread(target=self._run_event_loop, daemon=True)
        self.loop_thread.start()
        self.running = True

    @property
    def devices(self) -> list[ScanRecord]:
        if self.show_all_devices:
            return self.scan_records
        return [record for record in self.scan_records if record.scale_hint]

    @property
    def selected_record(self) -> ScanRecord | None:
        devices = self.devices
        if 0 <= self.selected_index < len(devices):
            return devices[self.selected_index]
        return None

    @property
    def selected_device(self) -> Any | None:
        record = self.selected_record
        return record.device if record else None

    @staticmethod
    def _scale_hint(device: Any, advertisement: Any) -> str | None:
        uuids = {
            short_uuid(value)
            for value in [*advertisement.service_uuids, *advertisement.service_data.keys()]
        }
        if WEIGHT_SERVICE_UUID in uuids:
            return "Служба Weight Scale 181D · ожидается характеристика 2A9D"
        if BODY_COMPOSITION_SERVICE_UUID in uuids:
            return "Mi Body Composition 181B · формат может отличаться от 2A9D"

        name = (device.name or advertisement.local_name or "").casefold()
        if any(token in name for token in ("mi scale", "miscal", "xmtzc", "mibfs", "mibcs")):
            return "Название похоже на Xiaomi Mi Scale"
        if "xiaomi" in name and any(token in name for token in ("scale", "weight", "вес")):
            return "Название похоже на Xiaomi Mi Scale"
        return None

    @staticmethod
    def _record_summary(record: ScanRecord) -> str:
        services = sorted({
            short_uuid(value)
            for value in [*record.advertisement.service_uuids, *record.advertisement.service_data.keys()]
        })
        service_text = f"UUID рекламы: {', '.join(value.upper() for value in services)}" if services else "UUID рекламы не указан"
        data_lengths = [f"{short_uuid(uuid).upper()} {len(data)}B"
                        for uuid, data in record.advertisement.service_data.items()]
        data_text = f" · данные: {', '.join(data_lengths)}" if data_lengths else ""
        return f"{record.scale_hint or 'BLE-признак весов не найден'} · {service_text}{data_text}"

    def _read_saved_address(self) -> str:
        try:
            with open(os.path.join(application_directory(), "config.json"), "r", encoding="utf-8") as file:
                config = json.load(file)
            address = config.get("address")
            return str(address) if address else "не задан"
        except Exception:
            return "не задан"

    def _run_event_loop(self) -> None:
        asyncio.set_event_loop(self.loop)
        self.loop.run_forever()

    def _submit(self, coroutine: Any) -> None:
        asyncio.run_coroutine_threadsafe(coroutine, self.loop)

    def scan(self) -> None:
        if self.busy or self.client is not None:
            self.status = "Сначала завершите текущее подключение."
            return
        self.busy = True
        self.status = "Ищу рекламу Xiaomi Mi Scale и совместимых BLE-весов…"
        self._submit(self._scan_devices())

    async def _scan_devices(self) -> None:
        try:
            found = await BleakScanner.discover(timeout=10.0, return_adv=True)
            records = [
                ScanRecord(device, advertisement, self._scale_hint(device, advertisement))
                for device, advertisement in found.values()
            ]
            self.events.put(("devices", (records, len(found))))
        except Exception as error:
            self.events.put(("error", f"Поиск не выполнен: {error}"))

    def connect_selected(self) -> None:
        device = self.selected_device
        if device is None or self.busy or self.client is not None:
            return
        self.busy = True
        self.weight = "—"
        self.packet_line = "Ожидаю первое уведомление с весов…"
        self.last_notification_time = None
        self.packet_count = 0
        self.status = f"Подключаюсь к {device.address}…"
        self._submit(self._connect_and_monitor(device.address))

    def probe_unknown(self) -> None:
        if self.busy or self.client is not None:
            return
        candidates = [record for record in self.scan_records if not record.scale_hint]
        if not candidates:
            self.status = "Нет неизвестных устройств для проверки. Сначала запустите поиск."
            return
        self.busy = True
        self.status = f"Проверяю {len(candidates)} устройств на GATT 2A9D по одному…"
        self._submit(self._probe_unknown_devices(candidates))

    async def _probe_unknown_devices(self, candidates: list[ScanRecord]) -> None:
        matches: set[str] = set()
        total = len(candidates)
        for index, record in enumerate(candidates, start=1):
            address = record.device.address
            self.events.put(("probe_progress", (index, total, record_name(record))))
            # Reuse the BLEDevice received from the advertisement scan. Passing
            # an address string makes Bleak perform an implicit rediscovery on
            # Windows, and the old 5 s timeout also covered GATT enumeration.
            # Slow adapters could therefore be reported as non-scales even
            # after they had already been seen in the scan.
            client = BleakClient(record.device, timeout=30.0)
            try:
                await client.connect()
                if client.is_connected:
                    has_weight_measurement = any(
                        characteristic.uuid.lower() == CHAR_UUID
                        for service in client.services
                        for characteristic in service.characteristics
                    )
                    if has_weight_measurement:
                        matches.add(address.upper())
            except Exception:
                pass
            finally:
                try:
                    if client.is_connected:
                        await client.disconnect()
                except Exception:
                    pass
        self.events.put(("probe_complete", (matches, total)))

    async def _disconnect_current(self) -> None:
        client = self.client
        self.client = None
        self.connected_address = None
        if client is None:
            return
        try:
            if client.is_connected:
                await client.stop_notify(CHAR_UUID)
                await client.disconnect()
        except Exception:
            try:
                if client.is_connected:
                    await client.disconnect()
            except Exception:
                pass

    async def _connect_and_monitor(self, address: str) -> None:
        try:
            await self._disconnect_current()
            client = BleakClient(address, timeout=30)
            await client.connect()
            if not client.is_connected:
                raise RuntimeError("Устройство не установило соединение.")
            self.client = client
            self.connected_address = address
            await client.start_notify(CHAR_UUID, self._on_notification)
            self.events.put(("connected", address))
        except Exception as error:
            await self._disconnect_current()
            self.events.put(("error", f"Не удалось читать характеристику 2A9D: {error}"))

    def _on_notification(self, _sender: Any, data: bytearray) -> None:
        if len(data) < 3:
            self.events.put(("short_packet", bytes(data).hex(" ").upper()))
            return
        now = time.monotonic()
        interval = None if self.last_notification_time is None else (now - self.last_notification_time) * 1000
        self.last_notification_time = now
        self.packet_count += 1
        weight_kg = int.from_bytes(data[1:3], "little") / 200
        self.events.put(("reading", (weight_kg, bytes(data).hex(" ").upper(), interval, self.packet_count)))

    def save_selection(self) -> None:
        device = self.selected_device
        if device is None:
            return
        config_path = os.path.join(application_directory(), "config.json")
        config: dict[str, Any] = {}
        if os.path.isfile(config_path):
            try:
                with open(config_path, "r", encoding="utf-8") as file:
                    value = json.load(file)
                if isinstance(value, dict):
                    config.update(value)
            except (OSError, json.JSONDecodeError):
                pass
        config["address"] = device.address.upper()
        config.setdefault("rotated", False)
        temporary_path = config_path + ".tmp"
        try:
            with open(temporary_path, "w", encoding="utf-8") as file:
                json.dump(config, file, ensure_ascii=False, indent=2)
            os.replace(temporary_path, config_path)
            self.saved_address = device.address.upper()
            self.status = "Привязка сохранена. Киоск возьмёт её при следующем запуске."
        except Exception as error:
            self.status = f"Не удалось сохранить config.json: {error}"

    def disconnect(self) -> None:
        if self.client is None or self.busy:
            return
        self.busy = True
        self.status = "Отключаюсь от весов…"
        self._submit(self._disconnect_and_report())

    async def _disconnect_and_report(self) -> None:
        await self._disconnect_current()
        self.events.put(("disconnected", None))

    def _process_events(self) -> None:
        while True:
            try:
                kind, payload = self.events.get_nowait()
            except queue.Empty:
                break
            if kind == "devices":
                records, self.scanned_total = payload
                self.scan_records = sorted(
                    records,
                    key=lambda record: (
                        record.scale_hint is None,
                        -(record.advertisement.rssi or -100),
                        record_name(record).casefold(),
                        record.device.address,
                    ),
                )
                self.selected_index = -1
                self.list_offset = 0
                self.busy = False
                matched = len([record for record in self.scan_records if record.scale_hint])
                if matched:
                    self.status = f"Похожих на весы устройств: {matched} из {self.scanned_total}. Выберите нужное."
                else:
                    self.status = f"Признаков весов не найдено среди {self.scanned_total} устройств. Можно показать все BLE."
            elif kind == "connected":
                self.busy = False
                self.status = f"Подключено к {payload}. Показываю все уведомления 2A9D."
            elif kind == "disconnected":
                self.busy = False
                self.status = "Отключено. Можно снова искать или выбрать другие весы."
            elif kind == "reading":
                weight, raw_packet, interval, packet_number = payload
                self.weight = f"{weight:.2f} кг"
                interval_text = "первый пакет" if interval is None else f"интервал {interval:.0f} мс"
                self.packet_line = f"Пакет {packet_number} · {interval_text} · {raw_packet}"
            elif kind == "short_packet":
                self.packet_line = f"Короткий пакет: {payload or '(пусто)'}"
            elif kind == "probe_progress":
                index, total, name = payload
                self.status = f"Проверяю GATT 2A9D: {index}/{total} · {name[:38]}"
            elif kind == "probe_complete":
                matches, total = payload
                self.scan_records = [
                    replace(record, scale_hint="Найдена GATT-характеристика 2A9D")
                    if record.device.address.upper() in matches
                    else record
                    for record in self.scan_records
                ]
                self.show_all_devices = False
                filtered = self.devices
                self.selected_index = next(
                    (index for index, record in enumerate(filtered)
                     if record.device.address.upper() in matches),
                    -1,
                )
                self.list_offset = 0
                self.busy = False
                if matches:
                    self.status = f"Найдены устройства с GATT 2A9D: {len(matches)} из {total}. Выберите весы."
                else:
                    self.status = f"GATT 2A9D не найден в {total} неизвестных устройствах."
            elif kind == "error":
                self.busy = False
                self.status = payload

    def _layout(self) -> dict[str, pygame.Rect]:
        width, height = self.screen.get_size()
        margin = 34
        gap = 22
        top = 148
        footer_y = height - 92
        list_width = int((width - margin * 2 - gap) * 0.59)
        right_width = width - margin * 2 - gap - list_width
        return {
            "scan": pygame.Rect(width - margin - 172, 36, 172, 46),
            "list": pygame.Rect(margin, top, list_width, footer_y - top),
            "toggle": pygame.Rect(margin + list_width - 144, top + 9, 126, 32),
            "live": pygame.Rect(margin + list_width + gap, top, right_width, footer_y - top),
            "probe": pygame.Rect(margin + 196, height - 70, 220, 48),
            "connect": pygame.Rect(width - margin - 220, height - 70, 220, 48),
            "save": pygame.Rect(width - margin - 456, height - 70, 220, 48),
            "disconnect": pygame.Rect(margin, height - 70, 180, 48),
        }

    def _draw_text(self, text: str, pos: tuple[int, int], color: tuple[int, int, int] = INK,
                   font: pygame.font.Font | None = None) -> None:
        surface = (font or self.font).render(text, True, color)
        self.screen.blit(surface, pos)

    def _button(self, rect: pygame.Rect, label: str, enabled: bool = True, secondary: bool = False) -> None:
        bg = YELLOW if enabled and not secondary else PANEL_ALT if enabled else (228, 224, 213)
        pygame.draw.rect(self.screen, bg, rect, border_radius=12)
        pygame.draw.rect(self.screen, LINE, rect, width=1, border_radius=12)
        text = self.font.render(label, True, INK if enabled else MUTED)
        self.screen.blit(text, text.get_rect(center=rect.center))

    def _draw(self) -> None:
        self.screen.fill(BG)
        width, _height = self.screen.get_size()
        layout = self._layout()

        self._draw_text("ПРИВЯЗКА ВЕСОВ", (34, 29), INK, self.title_font)
        self._draw_text("Выберите устройство, проверьте показания и сохраните адрес для LEGO-киоска.",
                        (36, 84), MUTED, self.small_font)
        self._button(layout["scan"], "Искать весы", not self.busy and self.client is None)

        for name in ("list", "live"):
            pygame.draw.rect(self.screen, PANEL, layout[name], border_radius=16)
            pygame.draw.rect(self.screen, LINE, layout[name], width=1, border_radius=16)

        list_rect = layout["list"]
        heading = "ВСЕ BLE-УСТРОЙСТВА" if self.show_all_devices else "ПОХОЖИЕ НА ВЕСЫ УСТРОЙСТВА"
        self._draw_text(heading, (list_rect.x + 18, list_rect.y + 17), MUTED, self.small_font)
        self._button(layout["toggle"], "Только весы" if self.show_all_devices else "Показать все",
                     enabled=True, secondary=True)
        row_y = list_rect.y + 50
        row_h = 58
        max_rows = max(1, (list_rect.height - 60) // row_h)
        visible = self.devices[self.list_offset:self.list_offset + max_rows]
        clip = self.screen.get_clip()
        self.screen.set_clip(pygame.Rect(list_rect.x + 8, row_y, list_rect.width - 16, list_rect.height - 58))
        for offset, record in enumerate(visible):
            device = record.device
            index = self.list_offset + offset
            row = pygame.Rect(list_rect.x + 12, row_y + offset * row_h, list_rect.width - 24, row_h - 5)
            if index == self.selected_index:
                pygame.draw.rect(self.screen, PANEL_ALT, row, border_radius=9)
            label = record_name(record)
            rssi = getattr(record.advertisement, "rssi", getattr(device, "rssi", None))
            signal = f"RSSI {rssi} dBm" if rssi is not None else ""
            self._draw_text(label[:36], (row.x + 12, row.y + 7), INK)
            self._draw_text(f"{device.address}   {signal}", (row.x + 12, row.y + 31), MUTED, self.small_font)
        self.screen.set_clip(clip)
        if not self.devices:
            empty_message = (
                "Запустите поиск или включите «Показать все»."
                if not self.scan_records
                else f"Подходящих пока нет из {self.scanned_total}; включите «Показать все»."
            )
            self._draw_text(empty_message, (list_rect.x + 18, row_y + 12), MUTED, self.small_font)

        live_rect = layout["live"]
        self._draw_text("ПРОВЕРКА СВЯЗИ", (live_rect.x + 18, live_rect.y + 17), MUTED, self.small_font)
        self._draw_text(self.weight, (live_rect.x + 18, live_rect.y + 58), RED, self.number_font)
        packet_rect = pygame.Rect(live_rect.x + 18, live_rect.y + 126, live_rect.width - 36, 100)
        words = self.packet_line.split(" ")
        lines: list[str] = []
        line = ""
        for word in words:
            candidate = f"{line} {word}".strip()
            if self.small_font.size(candidate)[0] > packet_rect.width and line:
                lines.append(line)
                line = word
            else:
                line = candidate
        if line:
            lines.append(line)
        for index, line in enumerate(lines[:4]):
            self._draw_text(line, (packet_rect.x, packet_rect.y + index * 19), MUTED, self.small_font)
        divider_y = live_rect.y + live_rect.height - 99
        pygame.draw.line(self.screen, LINE, (live_rect.x + 18, divider_y),
                         (live_rect.right - 18, divider_y), width=1)
        saved = self.saved_address if len(self.saved_address) < 26 else self.saved_address[:23] + "…"
        self._draw_text(f"Сохранённый адрес: {saved}", (live_rect.x + 18, divider_y + 13), INK,
                        self.small_font)

        self._draw_text(self.status[:100], (36, layout["disconnect"].y - 27), MUTED, self.small_font)
        self._button(layout["disconnect"], "Отключиться", self.client is not None and not self.busy, True)
        has_unknown = any(not record.scale_hint for record in self.scan_records)
        self._button(layout["probe"], "Проверить неизвестные",
                     self.show_all_devices and has_unknown and not self.busy and self.client is None, True)
        self._button(layout["save"], "Сохранить выбор",
                     self.selected_device is not None and not self.busy)
        self._button(layout["connect"], "Подключить и проверить",
                     self.selected_device is not None and self.client is None and not self.busy, True)
        pygame.display.flip()

    def _handle_click(self, pos: tuple[int, int]) -> None:
        layout = self._layout()
        if layout["toggle"].collidepoint(pos):
            self.show_all_devices = not self.show_all_devices
            self.selected_index = -1
            self.list_offset = 0
            self.status = "Фильтр изменён. Запускать поиск заново не нужно."
            return
        if layout["scan"].collidepoint(pos):
            self.scan()
            return
        if layout["connect"].collidepoint(pos):
            self.connect_selected()
            return
        if layout["save"].collidepoint(pos):
            self.save_selection()
            return
        if layout["disconnect"].collidepoint(pos):
            self.disconnect()
            return
        if layout["probe"].collidepoint(pos):
            self.probe_unknown()
            return
        list_rect = layout["list"]
        row_y = list_rect.y + 50
        row_h = 58
        max_rows = max(1, (list_rect.height - 60) // row_h)
        if list_rect.collidepoint(pos) and pos[1] >= row_y:
            index = self.list_offset + (pos[1] - row_y) // row_h
            if index < len(self.devices) and index < self.list_offset + max_rows:
                self.selected_index = index
                record = self.selected_record
                device = record.device
                self.packet_line = self._record_summary(record)
                self.status = f"Выбрано: {record_name(record)} · {device.address}"

    def close(self) -> None:
        try:
            future = asyncio.run_coroutine_threadsafe(self._disconnect_current(), self.loop)
            future.result(timeout=4)
        except Exception:
            pass
        self.loop.call_soon_threadsafe(self.loop.stop)
        pygame.quit()

    def run(self) -> None:
        while self.running:
            self._process_events()
            for event in pygame.event.get():
                if event.type == pygame.QUIT:
                    self.running = False
                elif event.type == pygame.KEYDOWN:
                    if event.key == pygame.K_ESCAPE:
                        self.running = False
                    elif event.key == pygame.K_UP:
                        self.list_offset = max(0, self.list_offset - 1)
                    elif event.key == pygame.K_DOWN:
                        self.list_offset = min(max(0, len(self.devices) - 1), self.list_offset + 1)
                elif event.type == pygame.MOUSEWHEEL:
                    if event.y < 0:
                        self.list_offset = min(max(0, len(self.devices) - 1), self.list_offset + 1)
                    elif event.y > 0:
                        self.list_offset = max(0, self.list_offset - 1)
                elif event.type == pygame.MOUSEBUTTONDOWN and event.button == 1:
                    self._handle_click(event.pos)
            self._draw()
            self.clock.tick(60)
        self.close()


def main() -> None:
    ScaleSetup().run()


if __name__ == "__main__":
    main()
