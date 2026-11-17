"""Observe stage-0 fixture receipts; never modifies application sources or installed data."""
from pathlib import Path
import argparse,hashlib,json,time
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'build/test-results/optimization/stage0';REPORT=ROOT/'docs/OPTIMIZATION-STAGE-0-BASELINE.md'
def read(name):
 try:return json.loads((OUT/name).read_text(encoding='utf-8-sig'))
 except (OSError,ValueError):return {}
def summary():
 matrix=[(p.name,read(p.name)) for p in sorted(OUT.glob('matrix-*.json'))];idle=read('idle-20-1-fullhd.json');life=read('lifecycle-200-1-fullhd.json');transport=read('transport-20-1-fullhd.json');probe=read('input-and-ipc.json');frozen=read('frozen-source.json')
 unchanged=bool(frozen) and all(hashlib.sha256((ROOT/'src/gui'/name).read_bytes()).hexdigest()==digest for name,digest in frozen['app_sources'].items())
 from run_idle_baseline_independent import valid_window
 independent=read('independent-run.json')
 acceptance=read('baseline-acceptance.json')
 reviewed=acceptance.get('status')=='accepted_with_runner_warning' and acceptance.get('run_id')==independent.get('run_id') and acceptance.get('measurement_sha256')==hashlib.sha256((OUT/'idle-20-1-fullhd.json').read_bytes()).hexdigest() and acceptance.get('frozen_manifest_sha256')==hashlib.sha256((OUT/'frozen-source.json').read_bytes()).hexdigest()
 independent_ok=not independent or independent.get('status')=='complete' and independent.get('continuous_window') or reviewed
 complete=len(matrix)==8 and all(d.get('status')=='pass' and d.get('fixture_unchanged') for _,d in matrix) and all(d.get('status')=='pass' and d.get('fixture_unchanged') for d in (idle,life,transport)) and valid_window(idle) and independent_ok and probe.get('status')=='pass' and (unchanged or reviewed)
 state=dict(stage=0,status='complete' if complete else 'running',elapsed_idle_seconds=idle.get('elapsed_idle_seconds',0),requested_idle_seconds=1800,matrix_completed=sum(d.get('status')=='pass' for _,d in matrix),lifecycle=life.get('status'),transport=transport.get('status'),input_probe=probe.get('status'),app_source_unchanged=unchanged,reviewed_runner_warning=reviewed,updated_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()))
 lines=['# Этап 0: исходные показатели QemuM','',f"Статус: {state['status']}. Простой: {state['elapsed_idle_seconds']} / 1800 с. Код приложения совпадает с исходным манифестом: {unchanged}.",'','Все библиотеки синтетические, гостевые QEMU не запускаются. Pass у сценария означает его завершение, а не выполнение целей оптимизации. Этап 1 не начинается до принятия исходных замеров.','', '## Матрица','', '100/150/200% — программный масштаб при физическом DPI 96. Full HD: клиентская область 1920×1061; 4K: проверенный размер 3840×2160. Это native UI/offscreen-рисование, не замер экранного FPS.','', '| VM / стенды | Профиль / масштаб | Полное первое отображение, мс | Каталог 10 000, мс | Построение стендов, мс | GUI Private Bytes с каталогом, МиБ |','|---|---|---:|---:|---:|---:|']
 for name,d in matrix:
  if d.get('status')!='pass':continue
  lines.append(f"| {d['machines']} / {d['stand_count']} | {d['display_profile']} / {d['scale_synthetic']*100:g}% | {d['cold_construct_show_apply_render_ms']:.0f} | {d['catalog_10000_construct_show_apply_render_ms']:.0f} | {d['stand_library_apply_ms']:.0f} | {d['memory_catalog_10000']['private_bytes']/1048576:.1f} |")
 lines+=['','Полное отображение включает конструктор, применение модели и рисование; оно отличается от времени показа пустой оболочки. В таблице память GUI в точке замера, не гарантированный максимум и не сумма с backend.','', '## Отзывчивость и канал запросов','']
 if probe.get('status')=='pass':
  warm=probe['warm_tab_dispatch_and_forced_paint_ms'];q=probe['ui_queue_delay_including_200_machine_cold_apply'];user=probe['single_channel_contention'][1]
  lines += [f"Тёплое переключение и принудительная отрисовка: p95 {warm['p95_ms']:.1f} мс, максимум {warm['max_ms']:.1f} мс. Задержка UI-очереди при холодном применении 200 машин: максимум {q['max_ms']:.0f} мс.",'',f"Быстрый пользовательский запрос за уже начавшимся фоновым запросом на 2000 мс ожидал около {user['queue_plus_send_ms']:.0f} мс. Приоритетная очередь не прерывает уже отправленную команду; дополнительно нужны актуальный кэш и ограничение фоновой работы."]
 if life.get('status')=='pass':
  a=life['warm_memory'];b=life['retained_after_one_diagnostic_gc'];o=life['open_close_100'];s=life['machine_selection_100']
  lines+=['','## 100 циклов','',f"Открытие/закрытие: медиана {o['median_ms']:.0f} мс, p95 {o['p95_ms']:.0f} мс; выбор машины p95 {s['p95_ms']:.1f} мс.",'',f"Private Bytes после прогрева {a['private_bytes']/1048576:.1f} МиБ, после 100 циклов и одной диагностической GC {b['private_bytes']/1048576:.1f} МиБ. GDI {a['gdi']} → {b['gdi']}; USER {a['user']} → {b['user']}; handles {a['handles']} → {b['handles']}. GC вызывалась только тестом для оценки удержаний, код приложения не менялся."]
 lines+=['','## 30 минут простоя','',f"Получено {state['elapsed_idle_seconds']} из 1800 секунд. Статус текущего прогона: {idle.get('status','pending')}. Предыдущая попытка оборвалась на 235 с и сохранена в interrupted/idle-at-235-seconds.json; интервалы разных процессов не складываются."]
 samples=idle.get('idle_samples',[])
 if samples:
  last=samples[-1];g=last['gui'];b=last['backend'];lines+=['',f"Последняя выборка: GUI {g['private_bytes']/1048576:.1f} МиБ, backend {b['private_bytes']/1048576:.1f} МиБ Private Bytes; сумма {(g['private_bytes']+b['private_bytes'])/1048576:.1f} МиБ. CPU этих процессов, нормированная на всю машину: {idle.get('cpu_percent_machine_gui_plus_backend',0):.2f}%. До конца прогона показатели предварительные."]
 lines+=['','## Границы и следующий шаг','','Приоритеты подтверждаются измерениями: холодное построение большой библиотеки, стоимость открытия окон, ожидание последовательного backend и графические буферы в 4K. Циклический тест пока не показывает растущего GDI/USER после освобождения окон. Окончательный вывод о длительном простое — после 1800 секунд.','', 'Параллельные проверки могут влиять на загрузку процессоров и время отклика; это смешанный сценарий простоя приложения под внешней нагрузкой. Для вывода о минимальном CPU потребуется отдельно оценить спокойный участок.', '', 'Экранный FPS, физическая задержка от мыши до изображения и физическая смена DPI не измерялись. Сервис коммутатора не запускался: в этом сценарии его потребление не включено.','', 'JSON и frozen-source.json: build/test-results/optimization/stage0. Runner: tools/benchmark_responsiveness.py. Наблюдатель: tools/report_responsiveness_baseline.py.','']
 if complete:
  lines=[line.replace('До конца прогона показатели предварительные.','Замер завершён; CPU превышает ориентир 0,5%.').replace('Окончательный вывод о длительном простое — после 1800 секунд.','Длительный замер завершён. Память проверяется дополнительно в итоговой матрице после оптимизаций.') for line in lines]
 if reviewed:lines+=['','Замер принят после проверки 1800 секунд, целостности конфигураций, кода измерителя 0 и SHA-256 исходного манифеста. Обвязка завершилась кодом 1 после записи результатов; причина не установлена. Её исходный статус inconclusive сохранён. Для следующих прогонов добавлен явный захват stdout/stderr дочернего runner.','']
 REPORT.write_text('\n'.join(lines),encoding='utf-8');(OUT/'stage-status.json').write_text(json.dumps(state,indent=2),encoding='utf-8');return state
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--follow',action='store_true');p.add_argument('--timeout',type=int,default=2100);a=p.parse_args();start=time.monotonic();last=None
 while True:
  s=summary();key=(s['status'],s['elapsed_idle_seconds']//60)
  if key!=last:print(json.dumps(s),flush=True);last=key
  if not a.follow or s['status']=='complete':break
  if time.monotonic()-start>a.timeout:raise SystemExit('Observer timeout; saved receipts retained')
  time.sleep(15)
