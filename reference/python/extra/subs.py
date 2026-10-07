#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Скрипт для работы с субтитрами
1. Вшивание субтитров в видео через FFmpeg
2. Извлечение субтитров из видео с помощью MKVToolNix
"""

import os
import sys
import subprocess
import re
import threading
import time
from pathlib import Path
from typing import List, Tuple, Optional
from alive_progress import alive_bar


class SubtitleBurner:
    def extract_subtitles_with_mkvextract(self, video_path: Path, output_dir: Path) -> List[Path]:
        """
        Извлекает субтитры из видео с помощью mkvextract
        
        Args:
            video_path: Путь к видео файлу
            output_dir: Папка для сохранения субтитров
            
        Returns:
            Список путей к извлеченным субтитрам
        """
        try:
            # Получаем информацию о дорожках субтитров
            cmd_info = [
                'mkvinfo',
                '--output-mode', 'text',
                str(video_path)
            ]
            
            result = subprocess.run(cmd_info, capture_output=True, text=True)
            
            if result.returncode != 0:
                print(f"⚠️  Не удалось получить информацию о {video_path.name}")
                return []
            
            # Ищем дорожки субтитров
            subtitle_tracks = []
            lines = result.stdout.split('\n')
            current_track = None
            
            for line in lines:
                if 'Track type: subtitles' in line:
                    # Ищем номер дорожки
                    for prev_line in lines[:lines.index(line)]:
                        if 'Track number:' in prev_line:
                            track_num = prev_line.split(':')[-1].strip()
                            subtitle_tracks.append(track_num)
                            break
            
            if not subtitle_tracks:
                print(f"📝 В {video_path.name} не найдено дорожек субтитров")
                return []
            
            extracted_files = []
            
            # Извлекаем каждую дорожку субтитров
            for track_num in subtitle_tracks:
                output_name = f"{video_path.stem}_track_{track_num}.ass"
                output_path = output_dir / output_name
                
                cmd_extract = [
                    'mkvextract',
                    str(video_path),
                    'tracks',
                    f'{track_num}:{output_path}'
                ]
                
                result = subprocess.run(cmd_extract, capture_output=True, text=True)
                
                if result.returncode == 0 and output_path.exists():
                    extracted_files.append(output_path)
                    print(f"  ✅ Извлечен {output_name}")
                else:
                    print(f"  ❌ Не удалось извлечь дорожку {track_num} из {video_path.name}")
            
            return extracted_files
            
        except Exception as e:
            print(f"⚠️  Ошибка при извлечении субтитров из {video_path.name}: {e}")
            return []
    
    def extract_all_subtitles(self) -> None:
        """
        Извлекает субтитры из всех видео файлов в директории
        """
        print("🔍 Ищу видео файлы для извлечения субтитров...")
        
        video_files = []
        for ext in self.video_extensions:
            for video_file in self.input_dir.glob(f"*{ext}"):
                video_files.append(video_file)
        
        if not video_files:
            print("❌ Не найдено видео файлов для обработки")
            return
        
        print(f"📁 Найдено {len(video_files)} видео файлов")
        
        # Создаем папку для субтитров
        subtitles_dir = self.output_dir / "extracted_subtitles"
        subtitles_dir.mkdir(parents=True, exist_ok=True)
        
        total_extracted = 0
        
        for i, video_path in enumerate(video_files, 1):
            print(f"\n🎬 Обрабатываю файл {i}/{len(video_files)}: {video_path.name}")
            
            extracted_files = self.extract_subtitles_with_mkvextract(video_path, subtitles_dir)
            total_extracted += len(extracted_files)
        
        print(f"\n🎉 Извлечение завершено!")
        print(f"📁 Субтитры сохранены в: {subtitles_dir}")
        print(f"✅ Всего извлечено: {total_extracted} файлов субтитров")
    
    def __init__(self, input_dir: str, output_dir: str = None):
        """
        Инициализация обработчика субтитров
        
        Args:
            input_dir: Папка с видео и субтитрами
            output_dir: Папка для сохранения результата (если None, то рядом с исходным)
        """
        self.input_dir = Path(input_dir)
        self.output_dir = Path(output_dir) if output_dir else self.input_dir
        
        # Поддерживаемые форматы
        self.video_extensions = {'.mp4', '.avi', '.mkv', '.mov', '.wmv', '.flv', '.webm'}
        self.subtitle_extensions = {'.srt', '.ass', '.ssa', '.sub', '.vtt'}
        self.mks_extensions = {'.mks'}  # Файлы для конвертации в субтитры
        
        # Создаем выходную папку если её нет
        self.output_dir.mkdir(parents=True, exist_ok=True)
    
    def convert_mks_to_ass(self, mks_path: Path) -> Path:
        """
        Конвертирует .mks файл в .ass субтитры
        
        Args:
            mks_path: Путь к .mks файлу
            
        Returns:
            Путь к созданному .ass файлу
        """
        try:
            ass_path = mks_path.with_suffix('.ass')
            
            # Команда для извлечения субтитров из .mks
            cmd = [
                'ffmpeg',
                '-i', str(mks_path),
                '-map', '0:s:0',  # Извлекаем первую дорожку субтитров
                '-c:s', 'ass',    # Конвертируем в ASS
                '-y',             # Перезаписываем если существует
                str(ass_path)
            ]
            
            result = subprocess.run(cmd, capture_output=True, text=True)
            
            if result.returncode == 0 and ass_path.exists():
                return ass_path
            else:
                print(f"⚠️  Не удалось конвертировать {mks_path.name}: {result.stderr}")
                return None
                
        except Exception as e:
            print(f"⚠️  Ошибка при конвертации {mks_path.name}: {e}")
            return None

    def find_matching_files(self) -> List[Tuple[Path, Path]]:
        """
        Ищет пары видео-субтитры с одинаковыми названиями
        Автоматически конвертирует .mks файлы в .ass
        
        Returns:
            Список кортежей (видео_файл, субтитры_файл)
        """
        video_files = {}
        subtitle_files = {}
        
        # Собираем все видео файлы
        for ext in self.video_extensions:
            for video_file in self.input_dir.glob(f"*{ext}"):
                name = video_file.stem
                video_files[name] = video_file
        
        # Собираем все файлы субтитров
        for ext in self.subtitle_extensions:
            for sub_file in self.input_dir.glob(f"*{ext}"):
                name = sub_file.stem
                subtitle_files[name] = sub_file
        
        # Конвертируем .mks файлы в .ass
        print("🔄 Проверяю .mks файлы для конвертации...")
        for ext in self.mks_extensions:
            for mks_file in self.input_dir.glob(f"*{ext}"):
                name = mks_file.stem
                if name not in subtitle_files:  # Только если нет уже готовых субтитров
                    print(f"  📝 Конвертирую {mks_file.name} в .ass...")
                    ass_file = self.convert_mks_to_ass(mks_file)
                    if ass_file:
                        subtitle_files[name] = ass_file
                        print(f"  ✅ Создан {ass_file.name}")
        
        # Находим совпадения
        matches = []
        for name in video_files:
            if name in subtitle_files:
                matches.append((video_files[name], subtitle_files[name]))
        
        return matches
    
    def parse_ffmpeg_progress(self, line: str) -> dict:
        """
        Парсит строку прогресса FFmpeg
        
        Args:
            line: Строка вывода FFmpeg
            
        Returns:
            Словарь с данными прогресса
        """
        progress = {}
        
        # Парсим время: time=00:01:23.45
        time_match = re.search(r'time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})', line)
        if time_match:
            hours, minutes, seconds, centiseconds = map(int, time_match.groups())
            total_seconds = hours * 3600 + minutes * 60 + seconds + centiseconds / 100
            progress['time'] = total_seconds
        
        # Парсим битрейт: bitrate= 1234.5kbits/s
        bitrate_match = re.search(r'bitrate=\s*([\d.]+)\s*(\w+)bits/s', line)
        if bitrate_match:
            value, unit = bitrate_match.groups()
            progress['bitrate'] = f"{value} {unit}bits/s"
        
        # Парсим скорость: speed=2.5x
        speed_match = re.search(r'speed=\s*([\d.]+)x', line)
        if speed_match:
            progress['speed'] = f"{speed_match.group(1)}x"
        
        # Парсим FPS: fps=30.5
        fps_match = re.search(r'fps=\s*([\d.]+)', line)
        if fps_match:
            progress['fps'] = f"{fps_match.group(1)} fps"
        
        # Парсим размер: size= 12345kB
        size_match = re.search(r'size=\s*([\d.]+)\s*(\w+)', line)
        if size_match:
            value, unit = size_match.groups()
            progress['size'] = f"{value} {unit}"
        
        return progress

    def get_video_duration(self, video_path: Path) -> float:
        """
        Получает длительность видео в секундах
        
        Args:
            video_path: Путь к видео файлу
            
        Returns:
            Длительность в секундах или None если не удалось получить
        """
        try:
            cmd = [
                'ffprobe',
                '-v', 'quiet',
                '-show_entries', 'format=duration',
                '-of', 'csv=p=0',
                str(video_path)
            ]
            
            result = subprocess.run(cmd, capture_output=True, text=True)
            
            if result.returncode == 0:
                duration = float(result.stdout.strip())
                return duration
            else:
                return None
                
        except Exception:
            return None

    def format_time(self, seconds: float) -> str:
        """
        Форматирует время в MM:SS или HH:MM:SS
        
        Args:
            seconds: Время в секундах
            
        Returns:
            Отформатированная строка времени
        """
        if seconds < 0:
            return "00:00"
        
        hours = int(seconds // 3600)
        minutes = int((seconds % 3600) // 60)
        secs = int(seconds % 60)
        
        if hours > 0:
            return f"{hours:02d}:{minutes:02d}:{secs:02d}"
        else:
            return f"{minutes:02d}:{secs:02d}"

    def burn_subtitles_with_progress(self, video_path: Path, subtitle_path: Path, 
                                   output_path: Path, progress_callback=None, **ffmpeg_params) -> bool:
        """
        Вшивает субтитры в видео через FFmpeg с отображением прогресса
        
        Args:
            video_path: Путь к видео файлу
            subtitle_path: Путь к файлу субтитров
            output_path: Путь для сохранения результата
            progress_callback: Функция для обновления прогресса
            **ffmpeg_params: Дополнительные параметры FFmpeg
            
        Returns:
            True если успешно, False если ошибка
        """
        try:
            # Экранируем путь к субтитрам для FFmpeg
            subtitle_path_escaped = str(subtitle_path).replace('\\', '/').replace(':', '\\:')
            
            # Альтернативный способ - используем только имя файла если он в той же папке
            if subtitle_path.parent == video_path.parent:
                subtitle_filter = f'subtitles={subtitle_path.name}:force_style=\'PrimaryColour=&Hffffff,OutlineColour=&H000000,BackColour=&H000000,Outline=2,Shadow=1\''
            else:
                subtitle_filter = f'subtitles={subtitle_path_escaped}:force_style=\'PrimaryColour=&Hffffff,OutlineColour=&H000000,BackColour=&H000000,Outline=2,Shadow=1\''
            
            # Базовые параметры FFmpeg
            cmd = [
                'ffmpeg',
                '-i', str(video_path),
                '-vf', subtitle_filter,
                '-c:a', 'copy',  # Копируем аудио без перекодирования
                '-progress', 'pipe:1',  # Выводим прогресс в stdout
                '-y'  # Перезаписываем выходной файл
            ]
            
            # Добавляем пользовательские параметры
            for key, value in ffmpeg_params.items():
                if key == 'c:v':
                    cmd.extend(['-c:v', value])
                elif key == 'rc':
                    cmd.extend(['-rc', value])
                elif key == 'b:v':
                    cmd.extend(['-b:v', value])
                elif key == 'bufsize':
                    cmd.extend(['-bufsize', value])
                elif key == 'pix_fmt':
                    cmd.extend(['-pix_fmt', value])
                elif key == 'preset':
                    cmd.extend(['-preset', value])
                elif key == 'crf':
                    cmd.extend(['-crf', str(value)])
            
            cmd.append(str(output_path))
            
            # Запускаем FFmpeg с выводом прогресса
            process = subprocess.Popen(
                cmd, 
                stdout=subprocess.PIPE, 
                stderr=subprocess.STDOUT,
                text=True,
                bufsize=1,
                universal_newlines=True
            )
            
            # Читаем вывод и парсим прогресс
            while True:
                line = process.stdout.readline()
                if not line:
                    break
                
                # Парсим прогресс
                progress = self.parse_ffmpeg_progress(line)
                if progress and progress_callback:
                    progress_callback(progress)
            
            # Ждем завершения процесса
            return_code = process.wait()
            
            if return_code == 0:
                return True
            else:
                print(f"\n✗ Ошибка при обработке {video_path.name}")
                return False
                
        except Exception as e:
            print(f"\n✗ Ошибка: {e}")
            return False

    def burn_subtitles(self, video_path: Path, subtitle_path: Path, 
                      output_path: Path, **ffmpeg_params) -> bool:
        """
        Вшивает субтитры в видео через FFmpeg (без прогресса для обратной совместимости)
        """
        return self.burn_subtitles_with_progress(video_path, subtitle_path, output_path, None, **ffmpeg_params)
    
    def process_all(self, **ffmpeg_params) -> None:
        """
        Обрабатывает все найденные пары видео-субтитры
        
        Args:
            **ffmpeg_params: Параметры для FFmpeg
        """
        matches = self.find_matching_files()
        
        if not matches:
            print("❌ Не найдено пар видео-субтитры с одинаковыми названиями")
            return
        
        print(f"📁 Найдено {len(matches)} пар для обработки:")
        for video, subtitle in matches:
            print(f"  🎬 {video.name} + 📝 {subtitle.name}")
        
        print(f"\n🚀 Начинаю обработку с параметрами:")
        for key, value in ffmpeg_params.items():
            print(f"  {key}: {value}")
        
        success_count = 0
        
        for i, (video_path, subtitle_path) in enumerate(matches, 1):
            print(f"\n🎬 Обрабатываю файл {i}/{len(matches)}: {video_path.name}")
            
            # Получаем длительность видео для расчета оставшегося времени
            video_duration = self.get_video_duration(video_path)
            start_time = time.time()
            
            # Формируем имя выходного файла
            output_name = f"{video_path.stem}_with_subs{video_path.suffix}"
            output_path = self.output_dir / output_name
            
            # Создаем прогресс-бар для текущего файла
            with alive_bar(title=f"🎬 {video_path.name}", 
                          bar="smooth", spinner="dots", 
                          length=60, enrich_print=False, manual=True) as bar:
                
                # Функция для обновления прогресса
                def update_progress(progress_data):
                    info_parts = []
                    
                    if 'time' in progress_data:
                        current_time = progress_data['time']
                        elapsed_time = time.time() - start_time
                        
                        # Форматируем прошедшее время
                        elapsed_str = self.format_time(current_time)
                        info_parts.append(f"⏱️ {elapsed_str}")
                        
                        # Рассчитываем оставшееся время и прогресс
                        if video_duration and current_time > 0:
                            progress_ratio = current_time / video_duration
                            if progress_ratio > 0:
                                estimated_total_time = elapsed_time / progress_ratio
                                remaining_time = estimated_total_time - elapsed_time
                                remaining_str = self.format_time(remaining_time)
                                info_parts.append(f"⏳ {remaining_str} осталось")
                                
                                # Обновляем прогресс-бар
                                bar(progress_ratio)
                    
                    if 'bitrate' in progress_data:
                        info_parts.append(f"📊 {progress_data['bitrate']}")
                    
                    if 'speed' in progress_data:
                        info_parts.append(f"⚡ {progress_data['speed']}")
                    
                    if 'fps' in progress_data:
                        info_parts.append(f"🎯 {progress_data['fps']}")
                    
                    if 'size' in progress_data:
                        info_parts.append(f"💾 {progress_data['size']}")
                    
                    if info_parts:
                        bar.text(" | ".join(info_parts))
                
                # Обрабатываем файл с прогрессом
                if self.burn_subtitles_with_progress(video_path, subtitle_path, output_path, update_progress, **ffmpeg_params):
                    success_count += 1
                    bar(1.0)  # Завершаем прогресс-бар
                    bar.text(f"✅ Успешно создан: {output_path.name}")
                else:
                    bar.text(f"❌ Ошибка при обработке: {video_path.name}")
        
        print(f"\n🎉 Обработка завершена!")
        print(f"✅ Успешно: {success_count}/{len(matches)} файлов")
        if success_count < len(matches):
            print(f"❌ Ошибок: {len(matches) - success_count} файлов")


def show_menu():
    """Показывает главное меню"""
    print("\n" + "="*60)
    print("🎬 СКРИПТ ДЛЯ РАБОТЫ С СУБТИТРАМИ")
    print("="*60)
    print("1. 🔥 Вшить субтитры в видео")
    print("2. 📤 Извлечь субтитры из видео")
    print("3. ❌ Выход")
    print("="*60)

def get_current_directory():
    """Возвращает текущую директорию скрипта"""
    return str(Path.cwd())

def get_output_directory():
    """Возвращает папку для сохранения результата (рядом с исходным)"""
    return None  # None означает - сохранять рядом с исходными файлами

def get_ffmpeg_params():
    """Запрашивает параметры FFmpeg у пользователя"""
    print("\n⚙️  Настройки кодирования (Enter - использовать по умолчанию):")
    
    params = {}
    
    # Кодек
    codec = input("Кодек видео (по умолчанию: hevc_nvenc): ").strip()
    if codec:
        params['c:v'] = codec
    else:
        params['c:v'] = 'hevc_nvenc'
    
    # Битрейт
    bitrate = input("Битрейт видео (по умолчанию: 22M): ").strip()
    if bitrate:
        params['b:v'] = bitrate
    else:
        params['b:v'] = '22M'
    
    # Размер буфера
    bufsize = input("Размер буфера (по умолчанию: 40M): ").strip()
    if bufsize:
        params['bufsize'] = bufsize
    else:
        params['bufsize'] = '40M'
    
    # Пиксельный формат
    pix_fmt = input("Пиксельный формат (по умолчанию: yuv420p10le): ").strip()
    if pix_fmt:
        params['pix_fmt'] = pix_fmt
    else:
        params['pix_fmt'] = 'yuv420p10le'
    
    # Пресет
    preset = input("Пресет кодирования (Enter - пропустить): ").strip()
    if preset:
        params['preset'] = preset
    
    # CRF
    crf = input("Качество видео CRF 0-51 (Enter - пропустить): ").strip()
    if crf:
        try:
            params['crf'] = int(crf)
        except ValueError:
            print("⚠️  Неверное значение CRF, пропускаю...")
    
    return params

def main():
    """Главная функция с интерактивным меню"""
    print("🎬 Добро пожаловать в скрипт для работы с субтитрами!")
    
    while True:
        show_menu()
        
        try:
            choice = input("\nВыберите действие (1-3): ").strip()
        except KeyboardInterrupt:
            print("\n\n👋 До свидания!")
            break
        
        if choice == '1':
            # Режим вшивания субтитров
            print("\n🔥 РЕЖИМ ВШИВАНИЯ СУБТИТРОВ")
            
            # Проверяем наличие FFmpeg
            try:
                subprocess.run(['ffmpeg', '-version'], capture_output=True, check=True)
            except (subprocess.CalledProcessError, FileNotFoundError):
                print("❌ Ошибка: FFmpeg не найден. Установите FFmpeg и добавьте в PATH")
                input("Нажмите Enter для продолжения...")
                continue
            
            # Получаем параметры
            input_dir = get_current_directory()
            output_dir = get_output_directory()
            print(f"📁 Обрабатываю файлы из: {input_dir}")
            ffmpeg_params = get_ffmpeg_params()
            
            # Создаем обработчик и запускаем
            burner = SubtitleBurner(input_dir, output_dir)
            burner.process_all(**ffmpeg_params)
            
            input("\nНажмите Enter для возврата в меню...")
            
        elif choice == '2':
            # Режим извлечения субтитров
            print("\n📤 РЕЖИМ ИЗВЛЕЧЕНИЯ СУБТИТРОВ")
            
            # Проверяем наличие MKVToolNix
            try:
                subprocess.run(['mkvextract', '--version'], capture_output=True, check=True)
            except (subprocess.CalledProcessError, FileNotFoundError):
                print("❌ Ошибка: MKVToolNix не найден. Установите MKVToolNix и добавьте в PATH")
                print("Скачать можно с: https://mkvtoolnix.download/")
                input("Нажмите Enter для продолжения...")
                continue
            
            try:
                subprocess.run(['mkvinfo', '--version'], capture_output=True, check=True)
            except (subprocess.CalledProcessError, FileNotFoundError):
                print("❌ Ошибка: mkvinfo не найден. Установите MKVToolNix и добавьте в PATH")
                input("Нажмите Enter для продолжения...")
                continue
            
            # Получаем параметры
            input_dir = get_current_directory()
            output_dir = get_output_directory()
            print(f"📁 Обрабатываю файлы из: {input_dir}")
            
            # Создаем обработчик и запускаем
            burner = SubtitleBurner(input_dir, output_dir)
            burner.extract_all_subtitles()
            
            input("\nНажмите Enter для возврата в меню...")
            
        elif choice == '3':
            print("\n👋 До свидания!")
            break
            
        else:
            print("❌ Неверный выбор! Выберите 1, 2 или 3.")
            input("Нажмите Enter для продолжения...")


if __name__ == "__main__":
    main()
