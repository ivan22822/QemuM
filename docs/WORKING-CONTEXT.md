# QemuM: краткое рабочее состояние

Обновлено 31 октября 2026. Это индекс, не live-состояние: проверить Git.

- Checkout: отдельный worktree build/stands-execution основного репозитория.
- Ветка: codex/topology-interface; текущий HEAD и статус проверять через Git.
- Исходники: 0.1.0-alpha.6. Установленная версия этим не определяется.
- Не трогать пользовательские VM, установленное приложение, оригинальный pre-QMP backup и sources/.
- Текущая задача: подготовка alpha.6 и tools/prepare_release.py; опубликованность и установленную версию проверять отдельно.
- Интерфейс разбит на 4 части. Части 1–4 реализованы локально: управление VM и обзор
  схемы в главном окне; отдельное единое окно редактирования TCP, коммутатора,
  MCAST, NIC и порядка запуска. Узлы перемещаются, координаты сохраняются по
  стендам, добавлены масштаб, прокрутка, ручное обновление состояний QEMU и
  коммутатора, диагностика в окне сети. План, подтверждение, отмена и журнал
  операции находятся во вкладках карточки стенда; обзор VM показывает диск,
  ISO и сеть. 52/53 пункта (98%). Скриншоты в ignored build/test-results:
  alpha-main.png, stand-plan-inline.png, stand-journal-inline.png. Открыта только
  проверка на физических мониторах с разными DPI. Схема не подтверждает
  IP-связность гостевых ОС. Изолированная сборка, 311 unit-тестов и 13
  GUI-наборов прошли 31.10.2026.
- Для размытого интерфейса добавлен WinForms PerMonitorV2 через
  src/gui/VMManager.exe.config, target .NET Framework 4.8 и упаковку конфига.
  GUI-тест подтвердил реальный контекст DPI; 311 unit, 13 GUI и аудит локального
  патч-пакета прошли. Реальные разные DPI у друзей ещё не проверены.
- В начале новой задачи: краткая рекомендация модели/режима по описанию;
  при усложнении — уведомление о переключении. Правила: docs/MODEL-ROUTING.md.
- Также рекомендовать место работы: здесь / существующий чат / новый.
  Передача между связанными QemuM-чатами: docs/CHAT-ROUTING.md;
  проверенные адресаты — локальный ignored build/workflow/chat-links.json.
- L2: 94/98 (95,9%); отчёт docs/L2-SWITCH-LIVE-RESULTS.md. Реализованы schema 4, loopback switch, named pipe,
  native ownership, независимый lifecycle, план/редактор, упаковка.
- Историческая контрольная точка до оптимизаций: 289 unit, 13 GUI-наборов,
  core/service/stand QEMU, isolated upgrade и source/package audit прошли.
- Добавлены компактный runner и диагностика; полный набор теперь 309 unit; 13 GUI-наборов прошли на предыдущем этапе.
  Итог оптимизаций: docs/WORKFLOW-OPTIMIZATION-REPORT.md; свежие результаты —
  python tools/workflow.py last gui, без автоматического пропуска проверок.
- Не проверены: пользовательские гостевые ОС/idle scan, аппаратная матрица и полный DPI.
- Live Alpine 3.24.3 пройден: IPv4/IPv6/ARP/broadcast, SHA256 8 МиБ до/после
  аварии третьего гостя. Исправлена гонка pipe reconnect. Остались 8.7/10.4/12.6/12.7.


| Область | Читать по необходимости |
| --- | --- |
| Следующая работа L2 | docs/L2-SWITCH-DEVELOPMENT-CHECKLIST.md, docs/L2-SWITCH-60-POINT-REPORT.md |
| Сеть | docs/L2-SWITCH-CONTRACT.md, src/backend/vm_stand_plan.py, src/switch/ |
| План/восстановление | src/backend/vm_stand_runtime.py, vm_stand_lifecycle.py, vm_switch.py |
| Интерфейс стендов | src/gui/StandsUi.cs, tests/gui/StandsTests.cs |
| Снимки | docs/SNAPSHOT-DEVELOPMENT-CHECKLIST.md, src/backend/vm_snapshot* |
| Сборка/обновление | tools/build.py, tools/release.py, docs/UPDATE-PROTOCOL.md |
| Процесс | docs/WORKFLOW.md, docs/TASK-TEMPLATE.md, tools/workflow.py |

Пути — от checkout. Краткая команда: python tools/workflow.py context.
Обновлять этот файл на границе задачи; результаты команд хранить в ignored build/.

Idle scan на своих Alpine-гостях проверен: IP ID всегда 0; zombie непригоден.
Spoof SYN и ответный RST при открытом порте подтверждены. Положительный 12.7
остаётся открыт; детали docs/L2-IDLE-SCAN-RESULTS.md. Nmap не установлен.
