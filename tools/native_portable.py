"""Bounded native release builder; never migrates user state."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
import xml.etree.ElementTree as ET
import re
from pathlib import PurePosixPath

PROJECTS = ('MabiAgent.Core', 'MabiAgent.Workflows', 'MabiAgent.Windows', 'MabiAgent.Capture', 'MabiAgent.Desktop')
CONFIGS = ('screen_catalog.1280x960.tw.json', 'screen_overlays.1280x960.tw.json', 'screen_travel.1280x960.tw.json', 'screen_relogin.1280x960.tw.json', 'session_notifications.1280x960.tw.json', 'screen_purchase.1280x960.tw.json', 'client-inventory-schema.disk.json', 'items.verified.tw.json', 'item-name-candidates.tw.json')
MODEL = 'ch_PP-OCRv5_rec_mobile.onnx'


def user_instructions(version):
    return f'''MabiAgent {version} 使用说明

一、调整游戏画面
进入游戏，打开“环境设定 → 图形”：
1. 选择“视窗模式”。
2. “解析度”选择 1280×960。
3. “界面样式”选择 PC。
4. “调整比例”设为 100%。
应用设置后，让角色停在城镇等方便操作的位置。

二、打开工具
解压整个文件夹，双击 MabiAgent.exe。
首次启动请阅读“使用前须知”。如果提示需要安装 Npcap，点击其中的官方下载按钮，安装完成后重新打开工具。
Npcap 官方下载页：https://npcap.com/#download

三、添加任务并开始
工具会使用“私人剧场”道具主动换图抓包；若尚未获得该道具，请先完成照相任务解锁。

1. 点击顶部“查找”，选择游戏窗口。
2. 到“维护设置”查看修理、金币留存、背包整理和精灵之翼。有生活 VIP 时再打开对应开关。
3. 回到“任务队列”，点击“新增目标”。生产填成品件数，采集填轮数，钓鱼填时长。1 轮为 10 次采集尝试。
4. 点击“开始／继续”。当前任务和进度显示在窗口顶部。

默认使用后台触控，运行后可以切到其他程序。请保持游戏窗口展开；最小化后任务会暂停，还原窗口再点击继续。
想节省屏幕空间，可点“迷你窗口”。顶栏的主题按钮用于切换亮暗外观。

四、暂停与停止
临时离开或处理背包，请点“暂停”，回来后点“开始／继续”。
“停止全部”适合从头重跑：执行进度会清空，手动任务和设置保留。游戏里已经完成的购买、出售和加工仍然有效，再次开始可能再次花费材料或金币。
出现待处理提示时，点击消息按钮查看原因，处理后再继续。

五、帮助与更新
详细用法见 docs 文件夹中的“用户指南.md”。
队列和设置保存在 data 文件夹，也可从顶栏“数据目录”打开。更新前退出程序并备份 data，再把它复制到新版本目录。

本工具供个人研究和测试使用。使用自动化工具有账号受处罚或道具损失的风险，请自行决定是否使用。项目限非商业用途，完整许可见 docs/LICENSE.txt。
'''


def distribution_config(value):
    """Omit local provenance; retain runtime identities, quantities and settings."""
    metadata = {'evidence', 'provenance', 'review_report', 'review_preview', 'review',
                'source_manifest', 'live_evidence', 'basis', 'candidate_source',
                'product_evidence', 'verification_evidence', 'source_review_manifest',
                'metal_review_manifest', 'execution_note'}
    if isinstance(value, dict):
        result = {}
        for key, child in value.items():
            if key in metadata:
                continue
            if isinstance(child, str) and (key in {'source', 'label_source', 'scope_note'}):
                if re.search(r'user_confirmed:|用户|对话|本轮|[A-Z]:[\\/]|evidence/|build/|codex', child, re.I):
                    child = 'release_catalog' if key == 'source' else 'Reference metadata omitted from distribution.'
            result[key] = distribution_config(child)
        return result
    if isinstance(value, list):
        return [distribution_config(child) for child in value]
    return value


def package_launcher():
    return r'''@echo off
setlocal
cd /d "%~dp0"
set "MABI_STATE=%~dp0data\agent-run\desktop"
set "MABI_ARGS=%*"
:state_args
if "%~1"=="" goto state_ready
if /i "%~1"=="--state-dir" (
  if "%~2"=="" exit /b 1
  set "MABI_STATE=%~2"
  shift
)
shift
goto state_args
:state_ready
start "" "%~dp0MabiAgent.exe" %MABI_ARGS% --root "%~dp0." --state-dir "%MABI_STATE%"
'''
NATIVE_ASSETS = tuple('assets/templates/session_interruptions/'+name for name in ('session_expired_message.png','session_expired_end.png'))
INSTALL_FOLDERS = frozenset(('app', 'assets', 'config', 'models', 'docs', 'licenses', 'build-locks'))
INSTALL_FILES = frozenset(('MabiAgent.exe', 'Start-MabiAgent.cmd', '使用说明.txt'))


def update_destination(root, output=None):
    root = clean_path(root)
    output = clean_path(output or root/'发布包/MabiAgent-NET验收版')
    destination = clean_path(output/'MabiAgent')
    if not output.is_relative_to(root) or output == root or output.relative_to(root).parts[0] not in ('dist', 'build', '发布包'):
        raise ValueError('原位更新仅允许当前项目 dist、build 或发布包中的明确目录')
    if any(part.casefold() in ('data', '用户数据', '清理待删', '待清理') for part in destination.relative_to(root).parts):
        raise ValueError('原位更新不能进入用户数据或待删目录')
    if not destination.is_dir() or not (destination/'release-manifest.json').is_file():
        raise ValueError('原位更新要求已有安装清单')
    return destination


def _install_relative(value):
    if not isinstance(value, str) or not value or '\\' in value or ':' in value:
        raise ValueError('安装清单路径无效')
    relative = PurePosixPath(value)
    if relative.is_absolute() or any(p in ('..', '.') for p in relative.parts) or str(relative) != value:
        raise ValueError('安装清单路径越界')
    if any(p.endswith(('.', ' ')) or any(c in p for c in '<>"|?*') for p in relative.parts):
        raise ValueError('安装清单含Windows路径别名')
    if any(p.casefold() in ('data', '用户数据', '清理待删', '待清理') for p in relative.parts):
        raise ValueError('安装清单不得包含用户数据')
    if relative.parts[0] not in INSTALL_FOLDERS and value not in INSTALL_FILES:
        raise ValueError('安装清单含未知文件：'+value)
    return Path(*relative.parts)


def _manifest_files(directory, *, verify=False):
    directory = clean_path(directory)
    manifest = clean_path(directory/'release-manifest.json')
    document = json.loads(manifest.read_text(encoding='utf-8-sig'))
    if document.get('schema_version') != 1 or not isinstance(document.get('files'), list):
        raise ValueError('安装清单格式无效')
    rows, folded = {}, set()
    for row in document['files']:
        relative = _install_relative(row['path'])
        key = relative.as_posix().casefold()
        if key in folded:
            raise ValueError('安装清单路径重复')
        folded.add(key)
        path = clean_path(directory/relative)
        if not path.is_relative_to(directory):
            raise ValueError('安装资源路径越界')
        if verify and (not path.is_file() or path.stat().st_size != row['bytes'] or _file_hash(path) != row['sha256']):
            raise ValueError('安装资源校验失败：'+str(relative))
        rows[relative.as_posix()] = row
    return rows


def _file_hash(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def export_clean_release(directory, archive):
    """Export only verified install files. Local player data never enters the archive."""
    directory=clean_path(directory)
    archive=clean_path(archive)
    if archive.is_relative_to(directory):
        raise ValueError('交付压缩包必须位于安装目录外')
    rows=_manifest_files(directory, verify=True)
    temporary=archive.with_suffix(archive.suffix+'.tmp')
    if temporary.exists():
        raise FileExistsError('存在未完成交付压缩包')
    archive.parent.mkdir(parents=True,exist_ok=True)
    try:
        with zipfile.ZipFile(temporary,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=1) as output:
            for relative in sorted(rows):
                output.write(directory/relative,'MabiAgent/'+relative)
            output.write(directory/'release-manifest.json','MabiAgent/release-manifest.json')
        with zipfile.ZipFile(temporary) as check:
            if check.testzip() is not None:
                raise RuntimeError('交付压缩包校验失败')
        os.replace(temporary,archive)
    finally:
        if temporary.exists():
            temporary.unlink()
    return archive


def export_current_release(directory, version):
    """Keep one version-named package beside the installed application."""
    if not re.fullmatch(r'[0-9]+[.][0-9]+[.][0-9]+',version):
        raise ValueError('版本号须为三个数字段')
    directory=clean_path(directory)
    archive=export_clean_release(directory,directory.parent/f'MabiAgent-{version}-win-x64.zip')
    # Cleanup follows successful export. Never touch unrelated ZIP files or player data.
    for previous in archive.parent.iterdir():
        if previous==archive or not previous.is_file() or previous.is_symlink():
            continue
        if previous.name=='MabiAgent-干净交付.zip' or re.fullmatch(r'MabiAgent-[0-9]+[.][0-9]+[.][0-9]+-win-x64[.]zip',previous.name):
            previous.unlink()
    return archive


def _replace_install_file(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(target.name+'.mabi-update-tmp')
    if temporary.exists():
        raise FileExistsError('存在未处理更新临时文件：'+str(temporary))
    try:
        with source.open('rb') as src, temporary.open('xb') as dst:
            shutil.copyfileobj(src, dst)
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)


def _reviewed_install_receipt(receipt):
    if receipt is None:
        return {}
    document = json.loads(clean_path(receipt).read_text(encoding="utf-8-sig"))
    if not isinstance(document, dict) or document.get("schema_version") != 1 or not isinstance(document.get("files"), list):
        raise ValueError("审核安装凭据格式无效")
    allowed = {f"app/{project}.{extension}" for project in PROJECTS for extension in ("dll", "pdb")}
    allowed.add("app/MabiAgent.Desktop.runtimeconfig.json")
    rows = {}
    for row in document["files"]:
        if not isinstance(row, dict) or set(row) != {"path", "sha256"}:
            raise ValueError("审核安装凭据字段无效")
        name, digest = row["path"], row["sha256"]
        _install_relative(name)
        if name not in allowed or name in rows or not isinstance(digest, str) or len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
            raise ValueError("审核安装凭据路径或哈希无效")
        rows[name] = digest
    return rows


def install_native_update(root, destination, staged, reviewed_receipt=None):
    """Install only manifest-owned files; keep player state and unknown files in place."""
    destination = update_destination(root, Path(destination).parent)
    staged = clean_path(staged)
    if not staged.is_relative_to(clean_path(root)) or staged == destination or destination.is_relative_to(staged):
        raise ValueError('更新暂存路径越界')
    old = _manifest_files(destination)
    new = _manifest_files(staged, verify=True)
    reviewed = _reviewed_install_receipt(reviewed_receipt)
    for name, digest in reviewed.items():
        target = clean_path(destination/_install_relative(name))
        if name not in old or name not in new or not target.is_relative_to(destination) or not target.is_file() or _file_hash(target) != digest:
            raise ValueError("审核安装凭据已过期或不是两版共有文件："+name)
    if 'MabiAgent.exe' not in new:
        raise ValueError('更新清单缺少入口')
    check = json.loads((staged/'docs/self-check.json').read_text(encoding='utf-8-sig'))
    if check.get('ok') is not True or check.get('game_input') is not False:
        raise ValueError('更新暂存版本尚未通过离线自检')
    changed = []
    for name in sorted(set(old)|set(new)):
        target = clean_path(destination/_install_relative(name))
        if target.exists():
            if not target.is_file() or name not in old:
                raise ValueError('保留未知用户文件，拒绝覆盖：'+name)
            if _file_hash(target) != old[name]['sha256'] and reviewed.get(name) != _file_hash(target):
                raise ValueError('保留已修改安装文件，拒绝覆盖：'+name)
        if name not in new or not target.exists() or old.get(name, {}).get('sha256') != new[name]['sha256'] or name in reviewed:
            changed.append(name)
    rollback = clean_path(staged.parent/'rollback')
    rollback.mkdir(exist_ok=False)
    existed, touched = set(), []
    try:
        for name in changed+['release-manifest.json']:
            target = clean_path(destination/name)
            if target.is_file():
                backup = rollback/name;backup.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(target, backup);existed.add(name)
        for name in changed+['release-manifest.json']:
            target = clean_path(destination/name)
            if name in new or name == 'release-manifest.json':
                _replace_install_file(staged/name, target)
            else:
                target.unlink(missing_ok=True)
            touched.append(name)
    except BaseException as error:
        failures = []
        for name in reversed(touched):
            try:
                target = clean_path(destination/name)
                if name in existed:_replace_install_file(rollback/name, target)
                else:target.unlink(missing_ok=True)
            except BaseException as restore_error:failures.append(str(restore_error))
        if failures:
            raise RuntimeError('更新失败且回退未完成；保留唯一回退：'+str(rollback)+'；'+'；'.join(failures)) from error
        shutil.rmtree(rollback)
        raise
    shutil.rmtree(rollback)
    return destination


def update_native(root, output=None, reviewed_receipt=None, **options):
    _reviewed_install_receipt(reviewed_receipt)
    destination = update_destination(root, output)
    if any((prior/'rollback').exists() for prior in destination.parent.glob('.mabi-update-*')):
        raise RuntimeError('存在前次未完成回退，请先处理，不新增回退目录')
    lock = destination.parent/'.mabi-update.lock'
    with lock.open('x', encoding='utf-8') as handle:
        handle.write(str(os.getpid()));handle.flush()
        try:
            staging = Path(tempfile.mkdtemp(prefix='.mabi-update-', dir=destination.parent))
            try:
                built = build_native(root, staging, **options)
                return install_native_update(root, destination, built, reviewed_receipt=reviewed_receipt)
            except Exception:
                report=staging/'MabiAgent/docs/self-check.json'
                if report.is_file():
                    diagnostic=Path(root)/'build/package-self-check-latest.json'
                    diagnostic.parent.mkdir(parents=True,exist_ok=True)
                    shutil.copy2(report,diagnostic)
                raise
            finally:
                # A failed rollback is the only reason to retain this bounded staging directory.
                if not (staging/'rollback').exists():
                    shutil.rmtree(staging)
        finally:
            handle.close();lock.unlink()


def clean_path(path):
    """Refuse junctions before resolving; output must never point at user data."""
    path = Path(os.path.abspath(path))
    for part in (path, *path.parents):
        if part.is_symlink() or getattr(part, 'is_junction', lambda: False)():
            raise ValueError('不允许链接或目录联接：' + str(part))
    return path


def destination_for(root, output):
    root, output = clean_path(root), clean_path(output)
    destination = clean_path(output / 'MabiAgent')
    if any(part.lower() in ('data', '清理待删') for part in destination.parts):
        raise ValueError('发布目录不能位于用户数据或清理待删中')
    if output == root or root.is_relative_to(output):
        raise ValueError('发布目录不能覆盖源码根目录')
    if output.is_relative_to(root) and output.relative_to(root).parts[0] not in ('dist', 'build', '发布包'):
        raise ValueError('源码内发布目录只允许 dist、build 或发布包下的新目录')
    if destination.exists():
        raise FileExistsError('保留已有版本，请选择全新发布目录：' + str(destination))
    return destination


def source_file(root, relative):
    path = clean_path(Path(root) / relative)
    if not path.is_relative_to(clean_path(root)) or not path.is_file():
        raise FileNotFoundError('缺少安装资源或路径越界：' + str(relative))
    return path


def locked_packages(root):
    packages = {}
    for project in PROJECTS:
        lock = source_file(root, f'dotnet/{project}/packages.lock.json')
        for group in json.loads(lock.read_text(encoding='utf-8-sig'))['dependencies'].values():
            for name, value in group.items():
                if value['type'].lower() == 'project':
                    continue
                key = name.lower()
                if key in packages and packages[key][1] != value['resolved']:
                    raise ValueError('锁文件包版本不一致：' + name)
                packages[key] = (name, value['resolved'])
    return packages


def collect_assets(root):
    """Follow installed JSON references; never traverse data or user chain roots."""
    found, pending = {}, ['config/' + name for name in CONFIGS] + list(NATIVE_ASSETS)
    while pending:
        relative = pending.pop().replace('\\', '/')
        if relative in found:
            continue
        path = source_file(root, relative)
        if not relative.startswith(('config/', 'assets/templates/')):
            raise ValueError('非安装资源引用：' + relative)
        allowed = Path(root)/('config' if relative.startswith('config/') else 'assets/templates')
        if not path.is_relative_to(allowed):
            raise ValueError('安装资源引用越界：' + relative)
        found[relative] = path
        if path.suffix.lower() != '.json':
            continue
        document = json.loads(path.read_text(encoding='utf-8-sig'))
        def visit(value):
            if isinstance(value, dict):
                for key, child in value.items():
                    if key in ('extends', 'navigation_profile', 'profile') and isinstance(child, str) and child.endswith('.json'):
                        pending.append(child)
                    elif isinstance(child, str) and child.startswith('assets/templates/'):
                        pending.append(child)
                    else:
                        visit(child)
            elif isinstance(value, list):
                for child in value:
                    visit(child)
        visit(document)
    return found


def plan_native(root, output, *, model=None, model_license=None, nuget_cache=None):
    root = clean_path(root)
    destination = destination_for(root, output)
    files = collect_assets(root)
    chosen_model = clean_path(model) if model else root/'models'/MODEL
    if chosen_model is None or not chosen_model.is_file():
        raise FileNotFoundError('缺少 OCR 模型，请用 --model 指定')
    files['models/' + MODEL] = clean_path(chosen_model)
    source_notice = root/'models/ocr-model.source.json'
    if source_notice.is_file():
        files['models/ocr-model.source.json'] = source_notice
    for name in ('global.json', 'Directory.Build.props'):
        files['build-locks/' + name] = source_file(root, 'dotnet/' + name)
    packages = locked_packages(root)
    cache = clean_path(nuget_cache or os.environ.get('NUGET_PACKAGES', Path.home()/'.nuget/packages'))
    missing, licenses = [], []
    for project in PROJECTS:
        files[f'build-locks/{project}/packages.lock.json'] = source_file(root, f'dotnet/{project}/packages.lock.json')
    for key, (name, version) in sorted(packages.items()):
        folder = cache / key / version
        spec = folder / (key + '.nuspec')
        if not spec.is_file():
            missing.append('NuGet 包元数据：' + name + '/' + version)
            continue
        files[f'licenses/nuget/{name}/{version}/{spec.name}'] = clean_path(spec)
        document = ET.parse(spec)
        license_node = next((x for x in document.iter() if x.tag.split('}')[-1] == 'license'), None)
        license_type = license_node.attrib.get('type') if license_node is not None else None
        license_value = license_node.text if license_node is not None else None
        entry = dict(package=name, version=version, license_type=license_type, license=license_value)
        licenses.append(entry)
        if license_type == 'file':
            license_path = clean_path(folder / license_value)
            if not license_path.is_relative_to(folder) or not license_path.is_file():
                missing.append('许可文件：' + name)
            else:
                files[f'licenses/nuget/{name}/{version}/{license_path.name}'] = license_path
        elif not license_value:
            missing.append('许可声明：' + name)
        # Preserve shipped third-party notices alongside the package license metadata.
        for path in folder.rglob('*'):
            if path.is_file() and any(word in path.name.lower() for word in ('license', 'notice', 'copying')):
                files[f'licenses/nuget/{name}/{version}/' + path.relative_to(folder).as_posix()] = clean_path(path)
    license_path = clean_path(model_license) if model_license else root/'models'/(MODEL+'.LICENSE')
    if license_path.is_file():
        files['licenses/ocr-model.LICENSE'] = license_path
    else:
        missing.append('OCR 模型许可及来源说明：请用 --model-license 指定文件')
    files['docs/用户指南.md'] = source_file(root, 'README.md')
    files['docs/开发指南.md'] = source_file(root, 'docs/DEVELOPMENT.md')
    files['docs/LICENSE.txt'] = source_file(root, 'LICENSE')
    files['licenses/fonts/NotoSansSC-OFL.txt'] = source_file(root, 'dotnet/MabiAgent.Desktop/Assets/Fonts/OFL.txt')
    rows = [dict(source=str(source), path=relative, bytes=source.stat().st_size) for relative, source in sorted(files.items())]
    return dict(schema_version=1, runtime='win-x64', self_contained=True, destination=str(destination), project=str(root/'dotnet/MabiAgent.Desktop/MabiAgent.Desktop.csproj'), files=rows, packages=licenses, missing=missing, bytes=sum(r['bytes'] for r in rows), user_data_copied=False, pointer_changed=False, nuget_cache=str(cache))


def verify_restore(root):
    """RID restore may add runtime entries, but may not drift reviewed packages."""
    for project in PROJECTS:
        folder = Path(root)/'dotnet'/project
        original = json.loads((folder/'packages.lock.json').read_text(encoding='utf-8-sig'))['dependencies']
        generated = json.loads((folder/'obj/native-packages.lock.json').read_text(encoding='utf-8-sig'))['dependencies']
        for framework, group in original.items():
            actual = generated.get(framework)
            if actual is None:
                raise RuntimeError('原生还原缺少锁定目标：' + project + '/' + framework)
            for package, entry in group.items():
                if entry.get('type', '').lower() == 'project':
                    continue
                found = actual.get(package, {})
                if (found.get('resolved'), found.get('contentHash')) != (entry.get('resolved'), entry.get('contentHash')):
                    raise RuntimeError('原生还原偏离已审阅锁文件：' + project + '/' + package)
            added = set(actual)-set(group)
            if added:
                raise RuntimeError('原生还原增加未审阅包：' + ', '.join(sorted(added)))


def copy_planned(plan):
    destination = Path(plan['destination'])
    for row in plan['files']:
        relative = Path(row['path'])
        if relative.is_absolute() or '..' in relative.parts or relative.parts[0].lower() == 'data':
            raise ValueError('发布清单含不安全路径')
        clean_path(row['source'])
    clean_path(destination)
    destination.mkdir(parents=True, exist_ok=False)
    for row in plan['files']:
        relative = Path(row['path'])
        if relative.is_absolute() or '..' in relative.parts or relative.parts[0].lower() == 'data':
            raise ValueError('发布清单含不安全路径')
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        source_path=clean_path(row['source'])
        if row['path'].startswith('config/') and source_path.suffix == '.json':
            target.write_text(json.dumps(distribution_config(json.loads(source_path.read_text(encoding='utf-8-sig'))),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        else:
            with source_path.open('rb') as source, target.open('xb') as out:
                shutil.copyfileobj(source, out)
    return destination


def build_native(root, output, **options):
    plan = plan_native(root, output, **options)
    if plan['missing']:
        raise RuntimeError('交付依赖未齐：' + '；'.join(plan['missing']))
    if sys.platform != 'win32':
        raise RuntimeError('原生发布只支持 Windows')
    root = Path(root)
    # Native restore uses bounded obj locks; source locks remain untouched.
    restore = ['dotnet', 'restore', plan['project'], '-r', 'win-x64', '-p:SelfContained=true', '-p:RestoreLockedMode=false', '-p:NuGetLockFilePath=obj/native-packages.lock.json']
    subprocess.run(restore, cwd=root/'dotnet', check=True)
    verify_restore(root)
    destination = copy_planned(plan)
    command = ['dotnet', 'publish', plan['project'], '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:NuGetLockFilePath=obj/native-packages.lock.json', '-p:PortableEntryPath='+str(destination/'MabiAgent.exe'), '-o', str(destination/'app')]
    subprocess.run(command, cwd=root/'dotnet', check=True)
    for project in PROJECTS:
        target=destination/'build-locks'/project/'native-packages.lock.json'
        shutil.copy2(root/'dotnet'/project/'obj/native-packages.lock.json',target)
    return finish_native(root, destination, plan)


def finish_native(root, destination, plan):
    app=destination/'app'
    executable = destination/'MabiAgent.exe'
    if not executable.is_file(): raise RuntimeError('缺少根目录入口 MabiAgent.exe')
    for name in ('MabiAgent.Desktop.exe', 'MabiAgent.Desktop.dll', 'coreclr.dll', 'hostfxr.dll', 'onnxruntime.dll', 'OpenCvSharpExtern.dll'):
        if not (app/name).is_file():
            raise RuntimeError('发布产物缺少原生依赖：' + name)
    runtime=json.loads((app/'MabiAgent.Desktop.runtimeconfig.json').read_text(encoding='utf-8-sig'))
    for framework in runtime['runtimeOptions'].get('includedFrameworks', []):
        name=framework['name'].lower()+'.runtime.win-x64'
        folder=Path(plan['nuget_cache'])/name/framework['version']
        notices=[path for path in folder.rglob('*') if path.is_file() and any(word in path.name.lower() for word in ('license','notice'))]
        if not notices:
            raise RuntimeError('缺少 .NET 运行时许可：'+name+'/'+framework['version'])
        for source in notices:
            target=destination/'licenses/dotnet'/name/framework['version']/source.relative_to(folder)
            target.parent.mkdir(parents=True,exist_ok=True)
            shutil.copy2(source,target)
    docs=destination/'docs';docs.mkdir(exist_ok=True)
    (destination/'Start-MabiAgent.cmd').write_text(package_launcher(), encoding='utf-8')
    version=ET.parse(root/'dotnet/Directory.Build.props').findtext('.//Version') or '0.0.0'
    (destination/'使用说明.txt').write_text(user_instructions(version),encoding='utf-8-sig')
    (docs/'THIRD-PARTY-NOTICES.json').write_text(json.dumps(dict(packages=plan['packages'], npcap='Npcap 系统驱动单独安装，不随包分发；安装和使用须遵守其许可。', model='见 licenses/ocr-model.LICENSE；仅包含识别模型。'), ensure_ascii=False, indent=2), encoding='utf-8')
    # Isolated UI-only check: never starts capture, game input, or player data.
    with tempfile.TemporaryDirectory(prefix='mabi-native-check-') as state:
        report = docs/'self-check.json'
        subprocess.run([str(executable), '--self-check', '--interaction-check', '--native-check', '--root', str(destination), '--state-dir', state, '--report', str(report)], cwd=destination, check=True, timeout=300)
        checked = json.loads(report.read_text(encoding='utf-8-sig'))
        if checked.get('ok') is not True or checked.get('game_input') is not False or '原生 OCR 推理及 OpenCV 加载' not in checked.get('ui_checks', []):
            raise RuntimeError('原生离线自检未通过')
    rows = []
    for path in sorted(destination.rglob('*')):
        if path.name == 'release-manifest.json' or path.relative_to(destination).parts[0].lower() == 'data':
            continue
        if path.is_file():
            with path.open('rb') as stream:
                digest = hashlib.file_digest(stream, 'sha256').hexdigest()
            rows.append(dict(path=path.relative_to(destination).as_posix(), bytes=path.stat().st_size, sha256=digest))
    (destination/'release-manifest.json').write_text(json.dumps(dict(schema_version=1, built_at=int(time.time()), entry='Start-MabiAgent.cmd', runtime='win-x64', self_contained=True, game_input=False, pointer_changed=False, files=rows), ensure_ascii=False, indent=2), encoding='utf-8')
    return destination









