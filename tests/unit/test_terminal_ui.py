import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[1] / 'src' / 'backend'))
import vm
import vm_console
import vm_doctor
import vm_editor
import vm_library
import vm_profiles
import vm_runtime
import vm_snapshots
import vm_storage
import vm_style
import vm_ui


class PickerTests(unittest.TestCase):
    def setUp(self):
        self.rows = [('first', {'label': 'Первая VM', 'cores': 2, 'ram_mb': 4096, 'ui_color': '#F4BD62'}),
                     ('second', {'label': 'Вторая VM', 'cores': 4, 'ram_mb': 8192, 'ui_color': '#79B8FF'})]
        self.manager = Mock()
        self.manager.entries.return_value = [name for name, _ in self.rows]
        self.manager.menu_rows.return_value = self.rows

    def choose(self, answers, **kwargs):
        output = io.StringIO()
        with patch('builtins.input', side_effect=answers), contextlib.redirect_stdout(output):
            result = vm_ui.choose_machine(self.manager, 'Выбор тестовой VM', **kwargs)
        return result, output.getvalue()

    def test_every_selection_shows_labels_resources_and_context(self):
        result, output = self.choose(['2'])
        self.assertEqual(result, 'second')
        for value in ('QEMU', 'Первая VM', 'Вторая VM', '4096', '8192', 'Выбор тестовой VM'):
            self.assertIn(value, output)
        self.assertNotIn('A — добавить', output)
        self.manager.query_qmp_status.assert_not_called()

    def test_invalid_input_reprompts_without_choosing_another_vm(self):
        result, output = self.choose(['999', 'x', '1'])
        self.assertEqual(result, 'first')
        self.assertIn('Выбери номер', output)

    def test_empty_or_zero_cancel(self):
        for answer in ('', '0'):
            self.assertIsNone(self.choose([answer])[0])

    def test_doctor_all_is_explicit_and_zero_still_cancels(self):
        for answer in ('', '*', 'all'):
            self.assertEqual(self.choose([answer], allow_all=True)[0], '*')
        self.assertIsNone(self.choose(['0'], allow_all=True)[0])

    def test_selection_uses_the_identity_in_the_displayed_rows(self):
        self.manager.entries.side_effect = [['first', 'second'], ['second', 'first']]
        self.assertEqual(self.choose(['1'])[0], 'first')

    def test_doctor_calls_only_the_selected_vm(self):
        with patch.object(vm_doctor, 'choose_machine', return_value='second'), \
                patch.object(vm_doctor, 'Doctor') as doctor, patch('builtins.input', return_value=''), \
                contextlib.redirect_stdout(io.StringIO()):
            vm_doctor.menu(self.manager)
        doctor.return_value.collect.assert_called_once_with('second')

    def test_live_pagination_preserves_global_numbers_and_blocks_main_actions(self):
        drawn = []
        keys = iter([']', 'd', '3', '\r'])
        class Terminal:
            def __init__(self, name): pass
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def title(self, value): pass
            def size(self): return os.terminal_size((70, 20))
            def draw(self, lines): drawn.append('\n'.join(lines))
            def key(self): return next(keys)
        monitor = Mock()
        monitor.snapshot.return_value = {'third': 'running'}
        library = vm_library.LibraryMenu(None, None, Terminal, lambda *args: monitor)
        library.live = True
        rows = self.rows + [('third', dict(label='Третья VM', ram_mb=1024, cores=1))] * 4
        with library:
            result = library.choose(rows, 'normal', False, purpose='Удаление VM')
        self.assertEqual(result, '3')
        self.assertTrue(any(' 3 ' in frame for frame in drawn))
        monitor.start.assert_called_once()
        monitor.close.assert_called_once()

    def test_cancel_main_delete_never_reaches_delete(self):
        with patch.object(vm, 'entries', return_value=['first', 'second']), patch.object(vm, 'menu_rows', return_value=self.rows), \
                patch.object(vm, 'remove_vm') as remove, patch('builtins.input', side_effect=['d', '0', '0']), \
                contextlib.redirect_stdout(io.StringIO()):
            vm.menu()
        remove.assert_not_called()

    def test_main_delete_uses_chosen_name_without_implicit_delete_confirmation(self):
        with patch.object(vm, 'entries', return_value=['first', 'second']), patch.object(vm, 'menu_rows', return_value=self.rows), \
                patch.object(vm, 'remove_vm') as remove, patch('builtins.input', side_effect=['d', '2', '0']), \
                contextlib.redirect_stdout(io.StringIO()):
            vm.menu()
        remove.assert_called_once_with('second')

    def test_all_optional_name_commands_cancel_without_initialization_or_action(self):
        for command in ('status', 'console', 'iso', 'disk', 'show', 'edit', 'remove', 'delete'):
            with self.subTest(command=command), patch.object(sys, 'argv', ['vm', command]), \
                    patch.object(vm, 'choose_machine', return_value=None) as chooser, \
                    patch.object(vm, 'initialize') as initialize:
                self.assertEqual(vm.main(), 0)
            chooser.assert_called_once()
            initialize.assert_not_called()

    def test_delete_preview_cannot_bypass_exact_name_confirmation(self):
        plan = dict(files=[Path('private.qcow2')], missing=[], kept=[(Path('shared.iso'), 'общий ISO')])
        with patch.object(vm, 'load', return_value=self.rows[0][1]), \
                patch.object(vm_storage, 'deletion_plan', return_value=plan), \
                patch.object(vm_storage, 'delete_with_template') as remove, \
                patch('builtins.input', side_effect=['Моя копия', 'wrong-name']), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            vm._remove_vm('first')
        remove.assert_not_called()
        for value in ('Будут удалены', 'private.qcow2', 'Будут сохранены', 'shared.iso'):
            self.assertIn(value, output.getvalue())


class PresentationTests(unittest.TestCase):
    def test_screens_and_cards_fit_narrow_terminals(self):
        data = dict(label='Длинное имя виртуальной машины', ram_mb=4096, cores=2,
                    disk_path='Z:/Some very long directory with spaces/' + 'disk-' * 35 + '.qcow2', iso_path='')
        for size in (24, 35, 52, 88, 110):
            frames = [vm_style.library_frame([('first', data)], size, False, purpose='Удаление VM'),
                      vm_editor.frame('first', data, True, size, False),
                      vm_ui.screen_frame('Подтверждение', sections=[('Полный путь', [data['disk_path']])],
                                         options=[('Enter', 'Назад')], width=size, ansi=False)]
            for frame in frames:
                self.assertTrue(all(len(line) <= size for line in frame), (size, frame))
            body = '\n'.join(frames[-1])
            self.assertNotIn('…', body)
            self.assertIn(re.sub(r'\s', '', data['disk_path']), re.sub(r'\s', '', body))

    def test_no_color_and_redirected_output_are_plain_text(self):
        with patch.dict(os.environ, NO_COLOR='1'), contextlib.redirect_stdout(io.StringIO()) as output:
            vm_ui.present('Проверка', sections=[('Текст', ['hello'])])
            vm_ui.notice('Готово', 'success')
        self.assertNotIn('\x1b', output.getvalue())

    def test_diagnostic_report_stays_private_after_reformatting(self):
        with tempfile.TemporaryDirectory(dir=HERE) as temp:
            manager = Mock()
            manager.ROOT = Path(temp) / 'machines'
            diagnostic = vm_doctor.Doctor(manager)
            diagnostic.add('SECRET-NAME', 'missing', 'error', 'PRIVATE TITLE', 'PRIVATE PATH', 'PRIVATE ADVICE')
            saved = diagnostic.save().read_text(encoding='utf-8')
            for text in ('SECRET-NAME', 'PRIVATE TITLE', 'PRIVATE PATH', 'PRIVATE ADVICE'):
                self.assertNotIn(text, saved)


if __name__ == '__main__':
    unittest.main(verbosity=2)
