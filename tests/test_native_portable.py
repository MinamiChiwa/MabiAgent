"""Native packaging safety checks use tiny temporary fixtures, never a real publish."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import hashlib
import zipfile
import xml.etree.ElementTree as ET
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('native_portable', Path(__file__).resolve().parents[1]/'tools/native_portable.py')
NATIVE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(NATIVE)
UPDATE_SPEC = importlib.util.spec_from_file_location('build_update_assets', Path(__file__).resolve().parents[1]/'tools/build_update_assets.py')
UPDATE_ASSETS = importlib.util.module_from_spec(UPDATE_SPEC)
UPDATE_SPEC.loader.exec_module(UPDATE_ASSETS)


class NativePortableTests(unittest.TestCase):
    def test_packaged_projects_cover_the_desktop_reference_graph(self):
        root=Path(__file__).resolve().parents[1]/'dotnet'
        pending=[root/'MabiAgent.Desktop/MabiAgent.Desktop.csproj'];projects=set()
        while pending:
            project=pending.pop().resolve()
            if project.stem in projects:continue
            projects.add(project.stem)
            pending.extend(project.parent/ref.attrib['Include'] for ref in ET.parse(project).getroot().iter('ProjectReference'))
        self.assertEqual(projects,set(NATIVE.PROJECTS))

    def test_current_export_names_version_and_removes_only_obsolete_packages(self):
        install=self.update_fixture(self.base/'install',{'MabiAgent.exe':b'entry'})
        for name in ('MabiAgent-0.5.3-win-x64.zip','MabiAgent-干净交付.zip','unrelated.zip'):
            (self.base/name).write_bytes(b'old')
        archive=NATIVE.export_current_release(install,'0.5.4')
        self.assertEqual('MabiAgent-0.5.4-win-x64.zip',archive.name)
        self.assertTrue(zipfile.is_zipfile(archive))
        self.assertFalse((self.base/'MabiAgent-0.5.3-win-x64.zip').exists())
        self.assertFalse((self.base/'MabiAgent-干净交付.zip').exists())
        self.assertTrue((self.base/'unrelated.zip').exists())
        NATIVE.export_current_release(install,'0.5.4')
        self.assertTrue(zipfile.is_zipfile(archive))

    def test_failed_current_export_retains_previous_packages(self):
        install=self.update_fixture(self.base/'install',{'MabiAgent.exe':b'entry'})
        old=self.base/'MabiAgent-0.5.3-win-x64.zip';old.write_bytes(b'old')
        (install/'MabiAgent.exe').write_bytes(b'modified')
        with self.assertRaises(ValueError):NATIVE.export_current_release(install,'0.5.4')
        self.assertEqual(b'old',old.read_bytes())
        with self.assertRaises(ValueError):NATIVE.export_current_release(install,'../invalid')

    def test_user_instructions_cover_game_setup_and_first_task(self):
        guide=NATIVE.user_instructions('0.5.2')
        self.assertIn('工具会通过“變更外貌/重生”进入重生页，按 ESC 取消并返回场景，刷新背包和仓库数据。',guide)
        for text in ('MabiAgent 0.5.2','环境设定 → 图形','视窗模式','1280×960','界面样式','PC','调整比例','100%','开始／继续'):
            self.assertIn(text,guide)
        for jargon in ('显式','自检','核账','原生','state-dir','不会'):
            self.assertNotIn(jargon,guide)
        self.assertIn('程序内更新会保留 data',guide)
        self.assertNotIn('再把它复制到新版本目录',guide)

    def test_distribution_preserves_verified_identity_without_private_provenance(self):
        original={'items':{'123':{'name':'材料','status':'verified','association':'user_confirmed','source':'user_confirmed:private conversation','evidence':[{'path':'build/private'}],'basis':'用户确认'}}}
        result=NATIVE.distribution_config(original)
        self.assertEqual({'name':'材料','status':'verified','association':'user_confirmed','source':'release_catalog'},result['items']['123'])
        self.assertIn('evidence',original['items']['123'])
        self.assertEqual({'source':'gathering','duration_s':61},NATIVE.distribution_config({'source':'gathering','duration_s':61}))
    def test_update_assets_reject_manifest_paths_outside_the_package_or_into_user_data(self):
        cases=(('../secret.txt', b'outside'),('/absolute.txt', b'absolute'),('data/work-queue.json', b'queue'),('assets/data/private.bin', b'nested'))
        for relative, content in cases:
            with self.subTest(relative=relative):
                package=self.base/('update-assets-'+str(abs(hash(relative))))
                package.mkdir()
                if relative.startswith('../'):
                    target=self.base/'secret.txt'
                elif relative.startswith('/'):
                    target=None
                else:
                    target=package/relative
                if target is not None:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(content)
                (package/'release-manifest.json').write_text(json.dumps({'files':[{'path':relative,'bytes':len(content),'sha256':hashlib.sha256(content).hexdigest()}]}),encoding='utf-8')
                with self.assertRaises(SystemExit):
                    UPDATE_ASSETS.load_baseline(package)

    def test_update_assets_reject_duplicate_case_insensitive_manifest_paths(self):
        package=self.update_fixture(self.base/'update-duplicates',{'config/file.json':b'{}'})
        digest=hashlib.sha256(b'{}').hexdigest()
        (package/'release-manifest.json').write_text(json.dumps({'files':[{'path':'config/file.json','bytes':2,'sha256':digest},{'path':'CONFIG/FILE.JSON','bytes':2,'sha256':digest}]}),encoding='utf-8')
        with self.assertRaises(SystemExit):
            UPDATE_ASSETS.load_baseline(package)

    def test_clean_export_excludes_local_queue_and_unknown_files(self):
        install=self.update_fixture(self.base/'install',{'MabiAgent.exe':b'entry','config/test.json':b'{}'})
        (install/'data').mkdir();(install/'data/work-queue.json').write_text('{"jobs":[1]}')
        (install/'debug-state.json').write_text('{"pending":true}')
        archive=NATIVE.export_clean_release(install,self.base/'clean.zip')
        with zipfile.ZipFile(archive) as package:
            self.assertEqual({'MabiAgent/MabiAgent.exe','MabiAgent/config/test.json','MabiAgent/docs/self-check.json','MabiAgent/release-manifest.json'},set(package.namelist()))
        self.assertTrue((install/'data/work-queue.json').exists())
        (install/'MabiAgent.exe').write_bytes(b'changed')
        with self.assertRaises(ValueError):NATIVE.export_clean_release(install,self.base/'invalid.zip')

    def test_clean_launcher_accepts_empty_state_without_inheriting_project_queue(self):
        launcher=NATIVE.package_launcher()
        self.assertNotIn('work-queue.json',launcher)
        self.assertNotIn('..\\..\\..',launcher)
        self.assertIn('--state-dir',launcher)

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='mabi-native-build-test-')
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.root = self.base/'source'
        (self.root/'config').mkdir(parents=True)
        (self.root/'docs').mkdir()
        (self.root/'README.md').write_text('User guide fixture',encoding='utf-8')
        (self.root/'README.zh-TW.md').write_text('Traditional Chinese guide fixture',encoding='utf-8')
        (self.root/'docs/DEVELOPMENT.md').write_text('Developer guide fixture',encoding='utf-8')
        (self.root/'LICENSE').write_text('License fixture',encoding='utf-8')
        for name in ('全部配方材料来源待填写.csv','全部配方材料来源核对.csv'):
            (self.root/'docs'/name).write_text('material,source',encoding='utf-8')
        (self.root/'docs/legacy-notes.md').write_text('extra documentation fixture',encoding='utf-8')
        for name in NATIVE.CONFIGS:
            (self.root/'config'/name).write_text('{}', encoding='utf-8')
        (self.root/'config/screen_catalog.1280x960.tw.json').write_text(json.dumps({'extends':'config/base.json','asset':{'path':'assets/templates/pixel.png'},'label_template':'assets/templates/legacy.png'}), encoding='utf-8')
        (self.root/'config/base.json').write_text('{}', encoding='utf-8')
        (self.root/'assets/templates').mkdir(parents=True)
        (self.root/'assets/templates/pixel.png').write_bytes(b'pixel-fixture')
        (self.root/'assets/templates/unreferenced.png').write_bytes(b'excluded')
        (self.root/'assets/templates/legacy.png').write_bytes(b'legacy')
        (self.root/'assets/templates/session_interruptions').mkdir()
        for name in ('session_expired_message.png','session_expired_end.png'):
            (self.root/'assets/templates/session_interruptions'/name).write_bytes(b'runtime-fixture')
        (self.root/'data').mkdir()
        (self.root/'data/user.json').write_bytes(b'keep')
        (self.root/'models').mkdir()
        (self.root/'models'/NATIVE.MODEL).write_bytes(b'model-fixture')
        self.license = self.root/'models'/ (NATIVE.MODEL+'.LICENSE')
        self.license.write_text('Fixture permission only', encoding='utf-8')
        (self.root/'dotnet').mkdir()
        for name in ('global.json','Directory.Build.props'):
            (self.root/'dotnet'/name).write_text('{}',encoding='utf-8')
        (self.root/'dotnet/Directory.Build.props').write_text('<Project><PropertyGroup><Version>0.5.1</Version></PropertyGroup></Project>',encoding='utf-8')
        for project in NATIVE.PROJECTS:
            folder=self.root/'dotnet'/project
            folder.mkdir()
            (folder/'packages.lock.json').write_text(json.dumps({'dependencies':{'net10.0':{'Example':{'type':'Direct','resolved':'1.0','contentHash':'fixed'}}}}),encoding='utf-8')
        (self.root/'dotnet/MabiAgent.Desktop/Assets/Fonts').mkdir(parents=True,exist_ok=True)
        (self.root/'dotnet/MabiAgent.Desktop/Assets/Fonts/OFL.txt').write_text('Font license fixture',encoding='utf-8')
        self.cache=self.base/'packages'
        folder=self.cache/'example/1.0'
        folder.mkdir(parents=True)
        (folder/'example.nuspec').write_text('<package><metadata><license type="expression">MIT</license></metadata></package>',encoding='utf-8')

    def plan(self, output=None):
        return NATIVE.plan_native(self.root,output or self.base/'release',nuget_cache=self.cache)

    def update_fixture(self, folder, files):
        folder.mkdir(parents=True, exist_ok=True)
        files = dict(files)
        files['docs/self-check.json'] = b'{"ok":true,"game_input":false}'
        rows = []
        for name, content in files.items():
            path=folder/name;path.parent.mkdir(parents=True, exist_ok=True);path.write_bytes(content)
            rows.append(dict(path=name,bytes=len(content),sha256=hashlib.sha256(content).hexdigest()))
        (folder/'release-manifest.json').write_text(json.dumps(dict(schema_version=1,files=rows)), encoding='utf-8')
        return folder

    def test_update_replaces_manifest_files_removes_retired_and_preserves_user_files(self):
        target=self.update_fixture(self.root/'发布包/current/MabiAgent', {'MabiAgent.exe':b'old','assets/old.png':b'retired'})
        (target/'data').mkdir();(target/'data/player.json').write_bytes(b'private')
        (target/'config').mkdir();(target/'config/user-extra.json').write_bytes(b'unknown')
        stage=self.update_fixture(self.root/'build/staging/MabiAgent', {'MabiAgent.exe':b'new','assets/new.png':b'current'})
        NATIVE.install_native_update(self.root,target,stage)
        self.assertEqual(b'new',(target/'MabiAgent.exe').read_bytes())
        self.assertFalse((target/'assets/old.png').exists())
        self.assertEqual(b'current',(target/'assets/new.png').read_bytes())
        self.assertEqual(b'private',(target/'data/player.json').read_bytes())
        self.assertEqual(b'unknown',(target/'config/user-extra.json').read_bytes())
        self.assertFalse((stage.parent/'rollback').exists())

    def test_update_failure_restores_installed_files_and_manifest(self):
        target=self.update_fixture(self.root/'发布包/current/MabiAgent', {'MabiAgent.exe':b'old','assets/old.png':b'retired'})
        original=(target/'release-manifest.json').read_bytes()
        stage=self.update_fixture(self.root/'build/staging/MabiAgent', {'MabiAgent.exe':b'new','assets/new.png':b'current'})
        replace=NATIVE._replace_install_file
        def fail_once(source,destination):
            if source==stage/'assets/new.png':raise OSError('fixture install failure')
            return replace(source,destination)
        with patch.object(NATIVE,'_replace_install_file',side_effect=fail_once),self.assertRaises(OSError):
            NATIVE.install_native_update(self.root,target,stage)
        self.assertEqual(b'old',(target/'MabiAgent.exe').read_bytes())
        self.assertEqual(b'retired',(target/'assets/old.png').read_bytes())
        self.assertEqual(original,(target/'release-manifest.json').read_bytes())
        self.assertFalse((target/'assets/new.png').exists())
        self.assertFalse((stage.parent/'rollback').exists())

    def test_update_rejects_unknown_collision_modified_install_and_unsafe_manifest(self):
        target=self.update_fixture(self.root/'发布包/current/MabiAgent', {'MabiAgent.exe':b'old'})
        stage=self.update_fixture(self.root/'build/staging/MabiAgent', {'MabiAgent.exe':b'new','assets/new.png':b'current'})
        (target/'assets').mkdir();(target/'assets/new.png').write_bytes(b'unknown')
        with self.assertRaises(ValueError):NATIVE.install_native_update(self.root,target,stage)
        self.assertEqual(b'old',(target/'MabiAgent.exe').read_bytes())
        (target/'assets/new.png').unlink();(target/'MabiAgent.exe').write_bytes(b'user edit')
        with self.assertRaises(ValueError):NATIVE.install_native_update(self.root,target,stage)
        self.assertEqual(b'user edit',(target/'MabiAgent.exe').read_bytes())
        for name in ('data/player.json','config/../../player.json','assets/../data/x','C:/outside','app/用户数据/x'):
            with self.subTest(name=name),self.assertRaises(ValueError):NATIVE._install_relative(name)

    def reviewed(self, rows):
        receipt=self.root/"reviewed.json"
        receipt.write_text(json.dumps(dict(schema_version=1,files=[dict(path=name,sha256=hashlib.sha256(content).hexdigest()) for name,content in rows.items()])),encoding="utf-8")
        return receipt

    def test_reviewed_project_hash_allows_exact_replacement_even_if_manifests_match(self):
        for project in ("MabiAgent.Core","MabiAgent.Update"):
            with self.subTest(project=project):
                name=f"app/{project}.dll"
                target=self.update_fixture(self.root/f"发布包/{project}/MabiAgent", {"MabiAgent.exe":b"old",name:b"manifest"})
                stage=self.update_fixture(self.root/f"build/{project}/MabiAgent", {"MabiAgent.exe":b"new",name:b"manifest"})
                (target/name).write_bytes(b"reviewed")
                NATIVE.install_native_update(self.root,target,stage,reviewed_receipt=self.reviewed({name:b"reviewed"}))
                self.assertEqual(b"manifest",(target/name).read_bytes())

    def test_reviewed_stale_hash_and_unowned_project_file_are_rejected(self):
        name="app/MabiAgent.Core.dll"
        target=self.update_fixture(self.root/"发布包/current/MabiAgent", {"MabiAgent.exe":b"old",name:b"manifest"})
        stage=self.update_fixture(self.root/"build/staging/MabiAgent", {"MabiAgent.exe":b"new",name:b"new"})
        (target/name).write_bytes(b"changed")
        with self.assertRaises(ValueError):NATIVE.install_native_update(self.root,target,stage,reviewed_receipt=self.reviewed({name:b"stale"}))
        (target/"app/MabiAgent.Windows.dll").write_bytes(b"unknown")
        with self.assertRaises(ValueError):NATIVE.install_native_update(self.root,target,stage,reviewed_receipt=self.reviewed({"app/MabiAgent.Windows.dll":b"unknown"}))
        self.assertEqual(b"old",(target/"MabiAgent.exe").read_bytes())

    def test_reviewed_receipt_cannot_allow_config_assets_data_or_aliases(self):
        for name in ("config/user.json","assets/user.png","data/player.json","app/Unknown.dll","app/../app/MabiAgent.Core.dll","app/MabiAgent.Core.DLL","app/MabiAgent.Desktop.runtimeconfig.json/extra"):
            with self.subTest(name=name),self.assertRaises(ValueError):NATIVE._reviewed_install_receipt(self.reviewed({name:b"x"}))

    def test_update_destination_cannot_escape_project_or_enter_state(self):
        for output in (self.base/'outside',self.root,self.root/'data',self.root/'tools'):
            with self.subTest(output=output),self.assertRaises(ValueError):NATIVE.update_destination(self.root,output)

    def test_update_wrapper_cleans_staging_after_success_and_build_failure(self):
        target=self.update_fixture(self.root/'发布包/current/MabiAgent', {'MabiAgent.exe':b'old'})
        def fake_build(root,output,**options):
            return self.update_fixture(output/'MabiAgent', {'MabiAgent.exe':b'new'})
        with patch.object(NATIVE,'build_native',side_effect=fake_build):
            self.assertEqual(target,NATIVE.update_native(self.root,target.parent))
        self.assertFalse(list(target.parent.glob('.mabi-update-*')))
        self.assertFalse((target.parent/'.mabi-update.lock').exists())
        with patch.object(NATIVE,'build_native',side_effect=RuntimeError('fixture build failed')),self.assertRaises(RuntimeError):
            NATIVE.update_native(self.root,target.parent)
        self.assertEqual(b'new',(target/'MabiAgent.exe').read_bytes())
        self.assertFalse(list(target.parent.glob('.mabi-update-*')))
        self.assertFalse((target.parent/'.mabi-update.lock').exists())

    def test_update_preserves_only_rollback_on_failed_restore_and_rejects_next_update(self):
        target=self.update_fixture(self.root/'发布包/current/MabiAgent', {'MabiAgent.exe':b'old'})
        def fake_build(root,output,**options):
            return self.update_fixture(output/'MabiAgent', {'MabiAgent.exe':b'new','assets/new.png':b'new asset'})
        replace=NATIVE._replace_install_file
        def fail(source,destination):
            if source.name=='new.png' or 'rollback' in source.parts:raise OSError('fixture file lock')
            replace(source,destination)
        with patch.object(NATIVE,'build_native',side_effect=fake_build),patch.object(NATIVE,'_replace_install_file',side_effect=fail),self.assertRaises(RuntimeError):
            NATIVE.update_native(self.root,target.parent)
        recovery=list(target.parent.glob('.mabi-update-*/rollback'))
        self.assertEqual(1,len(recovery))
        self.assertEqual(b'old',(recovery[0]/'MabiAgent.exe').read_bytes())
        with patch.object(NATIVE,'build_native') as build,self.assertRaises(RuntimeError):
            NATIVE.update_native(self.root,target.parent)
        build.assert_not_called()

    def test_update_rejects_invalid_stage_before_changing_installation(self):
        target=self.update_fixture(self.root/'发布包/current/MabiAgent', {'MabiAgent.exe':b'old'})
        stage=self.update_fixture(self.root/'build/staging/MabiAgent', {'MabiAgent.exe':b'new'})
        (stage/'MabiAgent.exe').write_bytes(b'corrupt')
        with self.assertRaises(ValueError):NATIVE.install_native_update(self.root,target,stage)
        self.assertEqual(b'old',(target/'MabiAgent.exe').read_bytes())

    def test_plan_is_read_only_and_follows_only_install_references(self):
        plan=self.plan()
        self.assertFalse((self.base/'release').exists())
        self.assertEqual([],plan['missing'])
        paths={row['path'] for row in plan['files']}
        self.assertIn('config/base.json',paths)
        self.assertIn('assets/templates/pixel.png',paths)
        self.assertIn('assets/templates/legacy.png',paths)
        self.assertIn('docs/用户指南.md',paths)
        self.assertIn('docs/使用指南.zh-TW.md',paths)
        self.assertFalse(any(name in path for path in paths for name in ('PROJECT_MEMORY','CURRENT_STATUS','TESTING','AGENTS','legacy-notes')))
        self.assertIn('docs/开发指南.md',paths)
        self.assertIn('docs/LICENSE.txt',paths)
        self.assertNotIn('docs/当前功能状态.md',paths)
        self.assertNotIn('assets/templates/unreferenced.png',paths)
        self.assertFalse(any(path.startswith('data/') for path in paths))
        self.assertEqual(b'keep',(self.root/'data/user.json').read_bytes())

    def test_public_docs_reject_developer_machine_paths_and_rewrite_language_links(self):
        (self.root/'README.md').write_text('[繁體](README.zh-TW.md) [开发](docs/DEVELOPMENT.md) [下载](https://example.com/releases)',encoding='utf-8')
        plan=self.plan()
        document=next(row['generated'] for row in plan['files'] if row['path']=='docs/用户指南.md')
        self.assertIn('](使用指南.zh-TW.md)',document)
        self.assertIn('](开发指南.md)',document)
        self.assertIn('](https://example.com/releases)',document)
        (self.root/'docs/DEVELOPMENT.md').write_text('本机证据 D:/Working/private/log.json',encoding='utf-8')
        with self.assertRaisesRegex(ValueError,'公开文档包含本机绝对路径'):self.plan()

    def test_previous_task_plans_and_execution_journals_never_enter_release(self):
        for state_root in ('data','用户数据'):
            for relative in ('work-queue.json','windows/scope/targets/old/batch.json','windows/scope/targets/old/production.json'):
                path=self.root/state_root/'agent-run/desktop'/relative
                path.parent.mkdir(parents=True,exist_ok=True)
                path.write_text('{"old_task":"private"}',encoding='utf-8')
        plan=self.plan()
        destination=NATIVE.copy_planned(plan)
        self.assertFalse((destination/'data').exists())
        self.assertTrue((self.root/'data/agent-run/desktop/work-queue.json').exists())

    def test_native_runtime_limit_templates_are_required_and_shipped(self):
        paths=set(NATIVE.collect_assets(self.root))
        for name in ('session_expired_message.png','session_expired_end.png'):
            self.assertIn('assets/templates/session_interruptions/'+name,paths)

    def test_existing_destination_is_rejected_even_when_empty(self):
        (self.base/'release/MabiAgent').mkdir(parents=True)
        with self.assertRaises(FileExistsError):
            self.plan()
        (self.base/'release/MabiAgent/user.txt').write_bytes(b'keep')
        with self.assertRaises(FileExistsError):
            self.plan()
        self.assertEqual(b'keep',(self.base/'release/MabiAgent/user.txt').read_bytes())

    def test_copy_excludes_user_state_and_cannot_replay(self):
        plan=self.plan()
        destination=NATIVE.copy_planned(plan)
        self.assertFalse((destination/'data').exists())
        self.assertEqual(b'pixel-fixture',(destination/'assets/templates/pixel.png').read_bytes())
        with self.assertRaises(FileExistsError):
            NATIVE.copy_planned(plan)
        self.assertEqual(b'keep',(self.root/'data/user.json').read_bytes())

    def test_output_cannot_target_source_data_or_cleanup(self):
        for path in (self.root,self.root/'data/new',self.root/'tools/new',self.base/'清理待删/new'):
            with self.subTest(path=path),self.assertRaises(ValueError):
                self.plan(path)

    def test_reference_cannot_escape_install_root(self):
        (self.root/'config/screen_catalog.1280x960.tw.json').write_text('{"extends":"../outside.json"}',encoding='utf-8')
        (self.base/'outside.json').write_text('{}',encoding='utf-8')
        with self.assertRaises((ValueError,FileNotFoundError)):
            self.plan()

    def test_template_reference_cannot_traverse_into_user_data(self):
        (self.root/'config/screen_catalog.1280x960.tw.json').write_text('{"asset":{"path":"assets/templates/../../data/user.json"}}',encoding='utf-8')
        with self.assertRaises(ValueError):
            self.plan()
        self.assertEqual(b'keep',(self.root/'data/user.json').read_bytes())

    def test_build_command_and_selfcheck_use_new_output_without_real_publish(self):
        calls=[]
        def run(command, **kwargs):
            calls.append(command)
            if command[:2]==['dotnet','restore']:
                for project in NATIVE.PROJECTS:
                    folder=self.root/'dotnet'/project
                    (folder/'obj').mkdir()
                    (folder/'obj/native-packages.lock.json').write_bytes((folder/'packages.lock.json').read_bytes())
            elif command[:2]==['dotnet','publish']:
                destination=Path(command[command.index('-o')+1]);destination.mkdir(parents=True,exist_ok=True)
                (destination.parent/'MabiAgent.exe').write_bytes(b'fake-entry')
                for name in ('MabiAgent.Desktop.exe','MabiAgent.Desktop.dll','coreclr.dll','hostfxr.dll','onnxruntime.dll','OpenCvSharpExtern.dll'):
                    (destination/name).write_bytes(b'fake-publish')
                (destination/'MabiAgent.Desktop.runtimeconfig.json').write_text('{"runtimeOptions":{"includedFrameworks":[]}}')
            elif '--self-check' in command:
                Path(command[command.index('--report')+1]).write_text(json.dumps({'ok':True,'game_input':False,'ui_checks':['原生 OCR 推理及 OpenCV 加载']}),encoding='utf-8')
            else:
                self.fail('unexpected command')
        with patch.object(NATIVE.subprocess,'run',side_effect=run),patch.object(NATIVE.sys,'platform','win32'):
            destination=NATIVE.build_native(self.root,self.base/'release',nuget_cache=self.cache)
        publish=calls[1]
        self.assertIn('--no-restore',publish)
        self.assertEqual('true',publish[publish.index('--self-contained')+1])
        self.assertEqual('win-x64',publish[publish.index('-r')+1])
        self.assertIn('MabiAgent 0.5.1',(destination/'使用说明.txt').read_text(encoding='utf-8-sig'))
        self.assertFalse((destination/'data').exists())
        self.assertFalse((self.root/'dist/current-release.txt').exists())
        self.assertEqual(b'keep',(self.root/'data/user.json').read_bytes())
        self.assertTrue((destination/'release-manifest.json').is_file())
        self.assertIn('MabiAgent.exe',(destination/'Start-MabiAgent.cmd').read_text())
        self.assertEqual(destination/'app',Path(publish[publish.index('-o')+1]))
        self.assertTrue((destination/'docs/self-check.json').is_file())
        (destination/'data').mkdir(exist_ok=True)
        (destination/'data/player.json').write_text('keep-user-state',encoding='utf-8')
        with patch.object(NATIVE.subprocess,'run',side_effect=run):
            NATIVE.finish_native(self.root,destination,{'nuget_cache':str(self.cache),'packages':[]})
        manifest=json.loads((destination/'release-manifest.json').read_text(encoding='utf-8'))
        self.assertNotIn('release-manifest.json',[row['path'] for row in manifest['files']])
        self.assertFalse(any(row['path'].startswith('data/') for row in manifest['files']))
        self.assertEqual('keep-user-state',(destination/'data/player.json').read_text())
    def test_missing_model_license_is_visible_without_mutation(self):
        self.license.unlink()
        self.assertTrue(any('OCR' in item for item in self.plan()['missing']))
        self.assertFalse((self.base/'release').exists())

    def test_restore_validation_rejects_package_drift(self):
        for project in NATIVE.PROJECTS:
            folder=self.root/'dotnet'/project
            (folder/'obj').mkdir()
            (folder/'obj/native-packages.lock.json').write_bytes((folder/'packages.lock.json').read_bytes())
        NATIVE.verify_restore(self.root)
        lock=self.root/'dotnet'/NATIVE.PROJECTS[0]/'obj/native-packages.lock.json'
        content=json.loads(lock.read_text());content['dependencies']['net10.0']['Example']['resolved']='2.0';lock.write_text(json.dumps(content))
        with self.assertRaises(RuntimeError):
            NATIVE.verify_restore(self.root)

    def test_restore_validation_allows_empty_rid_target_but_rejects_unreviewed_runtime_entries(self):
        for project in NATIVE.PROJECTS:
            folder=self.root/'dotnet'/project
            (folder/'obj').mkdir()
            (folder/'obj/native-packages.lock.json').write_bytes((folder/'packages.lock.json').read_bytes())
        lock=self.root/'dotnet'/NATIVE.PROJECTS[0]/'obj/native-packages.lock.json'
        content=json.loads(lock.read_text())
        content['dependencies']['net10.0/win-x64']={}
        lock.write_text(json.dumps(content))
        NATIVE.verify_restore(self.root)
        content['dependencies']['net10.0/win-x64']={'Microsoft.NETCore.App.Runtime.win-x64':{'type':'Direct','resolved':'10.0.0','contentHash':'runtime'}}
        lock.write_text(json.dumps(content))
        with self.assertRaisesRegex(RuntimeError,'未审阅包'):
            NATIVE.verify_restore(self.root)

    def test_custom_model_generates_matching_source_notice(self):
        default={'source':'reviewed-default','sha256':hashlib.sha256((self.root/'models'/NATIVE.MODEL).read_bytes()).hexdigest()}
        (self.root/'models/ocr-model.source.json').write_text(json.dumps(default),encoding='utf-8')
        custom=self.base/'custom-model.onnx';custom.write_bytes(b'custom-model')
        custom_license=self.base/'custom-model.LICENSE';custom_license.write_text('Custom model license and provenance',encoding='utf-8')
        plan=NATIVE.plan_native(self.root,self.base/'release',model=custom,model_license=custom_license,nuget_cache=self.cache)
        destination=NATIVE.copy_planned(plan)
        notice=json.loads((destination/'models/ocr-model.source.json').read_text(encoding='utf-8'))
        self.assertEqual(hashlib.sha256(b'custom-model').hexdigest(),notice['sha256'])
        self.assertEqual('custom',notice['source'])
        self.assertNotEqual(default['source'],notice['source'])

    def test_custom_model_without_license_is_missing_and_cannot_ship(self):
        custom=self.base/'custom-model.onnx';custom.write_bytes(b'custom-model')
        output=self.base/'release'
        plan=NATIVE.plan_native(self.root,output,model=custom,nuget_cache=self.cache)
        self.assertTrue(any('--model-license' in item for item in plan['missing']))
        self.assertFalse(any(row['path']=='licenses/ocr-model.LICENSE' for row in plan['files']))
        notice=next(row for row in plan['files'] if row['path']=='models/ocr-model.source.json')
        self.assertEqual(hashlib.sha256(b'custom-model').hexdigest(),json.loads(notice['generated'])['sha256'])
        with patch.object(NATIVE.subprocess,'run') as run,self.assertRaisesRegex(RuntimeError,'交付依赖未齐'):
            NATIVE.build_native(self.root,output,model=custom,nuget_cache=self.cache)
        run.assert_not_called()
        self.assertFalse(output.exists())

    def test_update_lock_probe_keeps_current_process_alive(self):
        import os
        lock=self.base/'update.lock';lock.write_text(str(os.getpid()),encoding='utf-8')
        self.assertTrue(NATIVE._update_lock_active(lock))

    def test_recover_update_removes_only_proven_stale_lock(self):
        target=self.update_fixture(self.root/'发布包/current/MabiAgent', {'MabiAgent.exe':b'old'})
        lock=target.parent/'.mabi-update.lock';lock.write_text('12345',encoding='utf-8')
        with patch.object(NATIVE,'_update_lock_active',return_value=True),self.assertRaises(RuntimeError):
            NATIVE.recover_native_update(self.root,target.parent)
        self.assertTrue(lock.exists())
        with patch.object(NATIVE,'_update_lock_active',return_value=False):
            self.assertEqual(target,NATIVE.recover_native_update(self.root,target.parent))
        self.assertFalse(lock.exists())
        lock.write_text('unknown',encoding='utf-8')
        with self.assertRaises(ValueError):NATIVE.recover_native_update(self.root,target.parent)
        self.assertEqual('unknown',lock.read_text(encoding='utf-8'))

    def failed_rollback(self):
        target=self.update_fixture(self.root/'发布包/current/MabiAgent', {'MabiAgent.exe':b'old'})
        (target/'data').mkdir();(target/'data/player.json').write_bytes(b'private')
        (target/'config').mkdir();(target/'config/user-extra.json').write_bytes(b'unknown')
        def fake_build(root,output,**options):
            return self.update_fixture(output/'MabiAgent', {'MabiAgent.exe':b'new','assets/new.png':b'new asset'})
        replace=NATIVE._replace_install_file
        def fail(source,destination):
            if source.name=='new.png' or 'rollback' in source.parts:raise OSError('fixture file lock')
            replace(source,destination)
        with patch.object(NATIVE,'build_native',side_effect=fake_build),patch.object(NATIVE,'_replace_install_file',side_effect=fail),self.assertRaises(RuntimeError):
            NATIVE.update_native(self.root,target.parent)
        return target

    def test_recover_update_restores_verified_rollback_and_preserves_user_files(self):
        target=self.failed_rollback()
        self.assertEqual(b'new',(target/'MabiAgent.exe').read_bytes())
        (target/'assets').mkdir();(target/'assets/new.png').write_bytes(b'new asset')
        (target/'release-manifest.json').unlink()
        self.assertEqual(target,NATIVE.recover_native_update(self.root,target.parent))
        self.assertEqual(b'old',(target/'MabiAgent.exe').read_bytes())
        self.assertFalse((target/'assets/new.png').exists())
        self.assertTrue((target/'release-manifest.json').is_file())
        self.assertEqual(b'private',(target/'data/player.json').read_bytes())
        self.assertEqual(b'unknown',(target/'config/user-extra.json').read_bytes())
        self.assertFalse(list(target.parent.glob('.mabi-update-*')))
        self.assertFalse((target.parent/'.mabi-update.lock').exists())

    def test_recover_update_refuses_unknown_staging_files_and_modified_targets(self):
        target=self.failed_rollback()
        staging=next(target.parent.glob('.mabi-update-*'))
        (staging/'unknown.txt').write_bytes(b'keep')
        with self.assertRaises(ValueError):NATIVE.recover_native_update(self.root,target.parent)
        self.assertEqual(b'new',(target/'MabiAgent.exe').read_bytes())
        self.assertEqual(b'keep',(staging/'unknown.txt').read_bytes())
        (staging/'unknown.txt').unlink();(target/'MabiAgent.exe').write_bytes(b'user edit')
        with self.assertRaises(ValueError):NATIVE.recover_native_update(self.root,target.parent)
        self.assertEqual(b'user edit',(target/'MabiAgent.exe').read_bytes())
        self.assertTrue((staging/'rollback').exists())


if __name__ == '__main__':
    unittest.main()
