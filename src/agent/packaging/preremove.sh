#!/bin/bash
set -e

systemctl stop vord-agent || true
systemctl disable vord-agent || true
