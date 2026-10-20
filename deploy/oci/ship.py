#!/usr/bin/env python3
"""GitHub runner -> optional BE ProxyJump -> OCI. No secret is interpolated into a shell script."""
import argparse, ipaddress, json, os, re, shlex, shutil, subprocess, sys, tempfile
from pathlib import Path
from urllib.parse import urlparse

def checked(argv, *, env=None, timeout=1800, input=None, show=False):
    p=subprocess.run(argv,env=env,input=input,text=True,capture_output=not show,timeout=timeout)
    if p.returncode: raise RuntimeError('transport command failed: '+argv[0]+' exit='+str(p.returncode))
    return '' if show else p.stdout

def is_canonical_native_origin(value):
    # native 입력은 패턴이나 URL 전체가 아닌 정규 HTTPS origin 하나만 허용한다.
    if not isinstance(value,str) or not value or not value.isascii() or any(ord(c)<=32 or ord(c)>=127 for c in value):
        return False
    if '*' in value or '%' in value:
        return False
    try:
        u=urlparse(value); host=u.hostname; port=u.port
        if u.scheme!='https' or not host or u.username is not None or u.password is not None:
            return False
        if u.path or u.params or u.query or u.fragment or port in (0,443):
            return False

        # IP와 DNS의 대체 표기를 정규화해서 받지 않는다. runtime의 Ordinal 비교와 같은 값을 전달한다.
        try:
            address=ipaddress.ip_address(host)
            canonical_host='['+str(address)+']' if address.version==6 else str(address)
        except ValueError:
            labels=host.split('.')
            if len(host)>253 or len(labels)<2 or labels[-1].isdigit() or re.fullmatch(r'0x[0-9a-f]+',labels[-1]):
                return False
            if any(re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?',label) is None for label in labels):
                return False
            canonical_host=host
        return value=='https://'+canonical_host+(':'+str(port) if port is not None else '')
    except ValueError:
        return False

def apply_native_origin(c, optional_secret):
    # LL은 선택 CHAT 입력을 해석하지 않고 기존 config 객체를 그대로 사용한다.
    if c.get('service') != 'chat':
        return c

    # 명시한 기존 필드는 별도 Secret으로 복구하지 않는다. 충돌도 조용히 덮어쓰지 않는다.
    existing = c.get('native_origin')
    if 'native_origin' in c and not is_canonical_native_origin(existing):
        raise RuntimeError('INVALID_NATIVE_ORIGIN')
    if optional_secret != '' and not is_canonical_native_origin(optional_secret):
        raise RuntimeError('INVALID_CHAT_NATIVE_ORIGIN')
    if existing and optional_secret and existing != optional_secret:
        raise RuntimeError('NATIVE_ORIGIN_CONFLICT')

    # browser 원문은 보존한다. 기존 배포 계약이 허용하는 마지막 slash만 비교에서 제외한다.
    selected = optional_secret if optional_secret else existing
    browser = c.get('browser_origin')
    if selected is not None and isinstance(browser, str) and selected == browser.rstrip('/'):
        raise RuntimeError('NATIVE_ORIGIN_DUPLICATES_BROWSER')
    if optional_secret == '':
        return c

    updated = dict(c)
    updated['native_origin'] = optional_secret
    return updated

def apply_mobile_push(c, enabled, app_environment, project_id):
    # LL과 미설정 CHAT은 기존 JSON 객체를 그대로 전달한다. 공개 Actions 설정만 선택적으로 소비한다.
    if c.get('service') != 'chat' or (enabled == '' and app_environment == '' and project_id == ''):
        return c
    if enabled not in ('true','false'):
        raise RuntimeError('INVALID_CHAT_MOBILE_PUSH_ENABLED')
    if app_environment not in ('development','preview','production'):
        raise RuntimeError('INVALID_CHAT_MOBILE_PUSH_ENVIRONMENT')
    if not isinstance(project_id,str) or re.fullmatch(r'[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}',project_id) is None:
        raise RuntimeError('INVALID_CHAT_MOBILE_PUSH_PROJECT')

    # 원래 설정의 잘못된 값이나 다른 프로젝트/환경을 공개 override로 조용히 복구하지 않는다.
    # enabled는 명시적 운영 스위치이므로 기존 true를 false로 끄는 전환도 허용한다.
    if 'mobile_push' in c:
        existing=c['mobile_push']
        if (not isinstance(existing,dict)
                or not set(existing)<= {'enabled','app_environment','project_id'}
                or type(existing.get('enabled',False)) is not bool):
            raise RuntimeError('INVALID_EXISTING_CHAT_MOBILE_PUSH')
        configured='app_environment' in existing or 'project_id' in existing
        if existing.get('enabled',False) or configured:
            old_project=existing.get('project_id')
            if (existing.get('app_environment') not in ('development','preview','production')
                    or not isinstance(old_project,str)
                    or re.fullmatch(r'[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}',old_project) is None):
                raise RuntimeError('INVALID_EXISTING_CHAT_MOBILE_PUSH')
            if existing['app_environment'] != app_environment or old_project != project_id:
                raise RuntimeError('CHAT_MOBILE_PUSH_CONFIG_CONFLICT')

    updated=dict(c)
    updated['mobile_push']={'enabled':enabled=='true','app_environment':app_environment,'project_id':project_id}
    return updated
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--service',choices=['ll','chat'],required=True)
    ap.add_argument('--image',required=True);a=ap.parse_args()
    sha=os.environ['GITHUB_SHA'];runid=os.environ['GITHUB_RUN_ID'];attempt=os.environ['GITHUB_RUN_ATTEMPT']
    if not re.fullmatch('[0-9a-f]{40}',sha) or not runid.isdigit() or not attempt.isdigit(): raise RuntimeError('invalid release identity')
    if not re.fullmatch(r'ghcr\.io/[a-z0-9_.-]+/[a-z0-9_.-]+@sha256:[0-9a-f]{64}',a.image): raise RuntimeError('immutable image required')
    host=str(ipaddress.IPv4Address(os.environ['OCI_HOST'])); user=os.environ.get('OCI_USERNAME','ubuntu')
    if user!='ubuntu': raise RuntimeError('this package requires reviewed ubuntu sudo access')
    release=f'{sha}-{runid}-{attempt}';remote=f'/home/ubuntu/.local/translacat-incoming/{release}'
    env=dict(os.environ);agent_pid=None
    with tempfile.TemporaryDirectory(prefix='tc-release-') as tmp:
        tmp=Path(tmp);os.chmod(tmp,0o700)
        def secret(name,value):
            p=tmp/name;p.write_text(value,encoding='utf-8');p.chmod(0o600);return p
        config=json.loads(os.environ['RUNTIME_CONFIG_JSON'])
        if config.get('service')!=a.service: raise RuntimeError('runtime service mismatch')
        if urlparse(config.get('public_url','')).hostname!=host: raise RuntimeError('runtime public URL must match OCI_HOST')
        # CHAT origin 검사는 runtime 파일·키·SSH 처리를 시작하기 전에 끝낸다.
        if a.service=='chat':
            config=apply_native_origin(config,os.environ.get('CHAT_NATIVE_ORIGIN',''))
            config=apply_mobile_push(config,os.environ.get('CHAT_MOBILE_PUSH_ENABLED',''),
                os.environ.get('CHAT_MOBILE_PUSH_APP_ENVIRONMENT',''),os.environ.get('CHAT_MOBILE_PUSH_PROJECT_ID',''))
        # Registry identity is derived from this workflow, not from an uploaded password.
        config['registry_user']=os.environ['GITHUB_ACTOR']
        runtime=secret('runtime.json',json.dumps(config,ensure_ascii=False))
        token=secret('registry-token',os.environ['GHCR_TOKEN'])
        secret('known_hosts',os.environ['SSH_KNOWN_HOSTS'].strip()+'\n')
        if 'PRIVATE KEY' in os.environ['SSH_KNOWN_HOSTS']: raise RuntimeError('known_hosts must contain server public host keys only')
        key=secret('target-key',os.environ['SSH_PRIVATE_KEY'].rstrip()+'\n')
        ask=tmp/'askpass.sh';ask.write_text('#!/bin/sh\nprintf "%s\\n" "$TC_KEY_PASSPHRASE"\n');ask.chmod(0o700)
        out=checked(['ssh-agent','-s'])
        env['SSH_AUTH_SOCK']=re.search(r'SSH_AUTH_SOCK=([^;]+);',out).group(1)
        agent_pid=int(re.search(r'SSH_AGENT_PID=(\d+);',out).group(1))
        env.update(SSH_ASKPASS=str(ask),SSH_ASKPASS_REQUIRE='force',DISPLAY='unused:0',TC_KEY_PASSPHRASE=os.environ.get('SSH_PASSPHRASE',''))
        try:
            checked(['ssh-add',str(key)],env=env,timeout=30,input='')
            # Separate target and jump keys. Agent forwarding is never enabled.
            common=f'''  User ubuntu
  Port 22
  BatchMode yes
  IdentitiesOnly yes
  StrictHostKeyChecking yes
  UserKnownHostsFile {tmp}/known_hosts
  GlobalKnownHostsFile /dev/null
  UpdateHostKeys no
  ForwardAgent no
  ConnectTimeout 15
  ServerAliveInterval 20
  ServerAliveCountMax 3
'''
            text=f'Host target\n  HostName {host}\n  IdentityFile {key}\n'+common
            jump=os.environ.get('BE_JUMP_HOST','').strip()
            if jump:
                jump=str(ipaddress.IPv4Address(jump));jk=secret('jump-key',os.environ['BE_JUMP_PRIVATE_KEY'].rstrip()+'\n')
                env['TC_KEY_PASSPHRASE']=os.environ.get('BE_JUMP_PASSPHRASE','')
                checked(['ssh-add',str(jk)],env=env,timeout=30,input='')
                text+='  ProxyJump jump\nHost jump\n  HostName '+jump+'\n  IdentityFile '+str(jk)+'\n'+common
            secret('ssh_config',text)
            ssh=['ssh','-F',str(tmp/'ssh_config')]
            checked(ssh+['target','umask 077; mkdir -p '+remote],env=env,timeout=45)
            checked(['scp','-q','-F',str(tmp/'ssh_config'),str(runtime),str(token),'deploy/oci/deploy.py','target:'+remote+'/'],env=env,timeout=60)
            command=['sudo','-n','python3',remote+'/deploy.py','--service',a.service,'--config',remote+'/runtime.json',
                '--image',a.image,'--sha',sha,'--release-id',release,'--registry-token',remote+'/registry-token']
            # deploy.py only emits allowlisted diagnostics; no remote docker/SQL output is streamed.
            checked(ssh+['target',shlex.join(command)],env=env,timeout=2400,show=True)
        finally:
            try: checked(['ssh','-F',str(tmp/'ssh_config'),'target','rm -f '+remote+'/runtime.json '+remote+'/registry-token'],env=env,timeout=30)
            except Exception: pass
            if agent_pid:
                try: os.kill(agent_pid,15)
                except ProcessLookupError: pass
    return 0
if __name__=='__main__':
    try: sys.exit(main())
    except Exception as e:
        # An exception object can contain SSH arguments; report its class, not secret-bearing input.
        print('RELEASE_TRANSPORT_FAILED: '+type(e).__name__+'; check SSH path/key/known_hosts and deployment status.',file=sys.stderr)
        sys.exit(1)
