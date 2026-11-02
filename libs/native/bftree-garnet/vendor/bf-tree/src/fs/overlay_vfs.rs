// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

use std::{
    fs::{File, OpenOptions},
    path::Path,
    sync::{
        atomic::{AtomicU64, AtomicUsize, Ordering},
        Mutex,
    },
};

#[cfg(unix)]
use std::os::unix::fs::FileExt;
#[cfg(windows)]
use std::os::windows::fs::FileExt;

use crate::{counter, nodes::DISK_PAGE_SIZE};

use super::{OffsetAlloc, VfsImpl};

static BASE_READ_BYTES: AtomicU64 = AtomicU64::new(0);
static WORK_READ_BYTES: AtomicU64 = AtomicU64::new(0);
static WORK_WRITE_BYTES: AtomicU64 = AtomicU64::new(0);
static COPY_UP_PAGES: AtomicU64 = AtomicU64::new(0);

/// Process-wide I/O counters of copy-on-write overlay trees.
#[repr(C)]
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct OverlayStats {
    pub base_read_bytes: u64,
    pub work_read_bytes: u64,
    pub work_write_bytes: u64,
    pub copy_up_pages: u64,
}

/// Returns the process-wide I/O counters of copy-on-write overlay trees.
pub fn overlay_stats() -> OverlayStats {
    OverlayStats {
        base_read_bytes: BASE_READ_BYTES.load(Ordering::Relaxed),
        work_read_bytes: WORK_READ_BYTES.load(Ordering::Relaxed),
        work_write_bytes: WORK_WRITE_BYTES.load(Ordering::Relaxed),
        copy_up_pages: COPY_UP_PAGES.load(Ordering::Relaxed),
    }
}

/// Copy-on-write storage over an immutable snapshot file: reads of disk pages never written come from the snapshot,
/// all writes go to a private work file at the same offsets. Opening a tree therefore never copies the snapshot.
/// The written-page bitmap lives only in memory, so the work file is meaningless without it; it is unlinked as soon
/// as it is opened and vanishes when the tree is dropped or the process dies.
pub(crate) struct OverlayVfs {
    base: Option<File>,
    base_len: AtomicUsize,
    work: File,
    written: Vec<AtomicU64>,
    copy_up_lock: Mutex<()>,
    offset_alloc: OffsetAlloc,
}

impl OverlayVfs {
    /// Opens `base_path` read-only and creates `work_path`, which must not exist.
    pub(crate) fn open_overlay(base_path: &Path, work_path: &Path) -> std::io::Result<Self> {
        let base = File::open(base_path)?;
        let base_len = base.metadata()?.len() as usize;
        let work = OpenOptions::new().read(true).write(true).create_new(true).open(work_path)?;
        // Best effort: on file systems without POSIX delete semantics the name stays until the handle closes.
        _ = std::fs::remove_file(work_path);
        let pages = base_len.div_ceil(DISK_PAGE_SIZE);
        Ok(Self {
            base: Some(base),
            base_len: AtomicUsize::new(base_len),
            work,
            written: (0..pages.div_ceil(64)).map(|_| AtomicU64::new(0)).collect(),
            copy_up_lock: Mutex::new(()),
            offset_alloc: OffsetAlloc::new_with(base_len),
        })
    }

    fn base_page_count(&self) -> usize {
        self.base_len.load(Ordering::Acquire).div_ceil(DISK_PAGE_SIZE)
    }

    fn is_written(&self, page: usize) -> bool {
        self.written[page / 64].load(Ordering::Acquire) & (1u64 << (page % 64)) != 0
    }

    fn mark_written(&self, page: usize) {
        self.written[page / 64].fetch_or(1u64 << (page % 64), Ordering::AcqRel);
    }

    /// Whether `page` is still served by the base file.
    fn reads_base(&self, page: usize) -> bool {
        page < self.base_page_count() && !self.is_written(page)
    }

    fn read_base(&self, offset: usize, buf: &mut [u8]) {
        let base = self.base.as_ref().unwrap();
        let available = self.base_len.load(Ordering::Acquire).saturating_sub(offset).min(buf.len());
        read_exact_at(base, &mut buf[..available], offset);
        buf[available..].fill(0);
        BASE_READ_BYTES.fetch_add(available as u64, Ordering::Relaxed);
    }

    /// Copies a base page that a write covers only partly into the work file, so its unwritten bytes survive.
    fn copy_up(&self, page: usize) {
        let _guard = self.copy_up_lock.lock().unwrap();
        if !self.reads_base(page) {
            return;
        }
        let mut buf = vec![0u8; DISK_PAGE_SIZE];
        self.read_base(page * DISK_PAGE_SIZE, &mut buf);
        write_all_at(&self.work, &buf, page * DISK_PAGE_SIZE);
        self.mark_written(page);
        COPY_UP_PAGES.fetch_add(1, Ordering::Relaxed);
    }
}

impl VfsImpl for OverlayVfs {
    fn read(&self, offset: usize, buf: &mut [u8]) {
        counter!(IOReadRequest);
        let mut pos = 0;
        while pos < buf.len() {
            let page = (offset + pos) / DISK_PAGE_SIZE;
            let from_base = self.reads_base(page);
            // Coalesce the run of pages served by the same file.
            let mut end = ((page + 1) * DISK_PAGE_SIZE - offset).min(buf.len());
            while end < buf.len() && self.reads_base((offset + end) / DISK_PAGE_SIZE) == from_base {
                end = (end + DISK_PAGE_SIZE).min(buf.len());
            }
            if from_base {
                self.read_base(offset + pos, &mut buf[pos..end]);
            } else {
                read_exact_at(&self.work, &mut buf[pos..end], offset + pos);
                WORK_READ_BYTES.fetch_add((end - pos) as u64, Ordering::Relaxed);
            }
            pos = end;
        }
    }

    fn write(&self, offset: usize, buf: &[u8]) {
        counter!(IOWriteRequest);
        let end = offset + buf.len();
        let first = offset / DISK_PAGE_SIZE;
        let last = end.div_ceil(DISK_PAGE_SIZE);
        for page in [first, last - 1] {
            let partial = offset > page * DISK_PAGE_SIZE || end < (page + 1) * DISK_PAGE_SIZE;
            if partial && self.reads_base(page) {
                self.copy_up(page);
            }
        }
        write_all_at(&self.work, buf, offset);
        WORK_WRITE_BYTES.fetch_add(buf.len() as u64, Ordering::Relaxed);
        for page in first..last.min(self.base_page_count()) {
            self.mark_written(page);
        }
    }

    fn alloc_offset(&self, size: usize) -> usize {
        self.offset_alloc.alloc(size)
    }

    fn dealloc_offset(&self, offset: usize) {
        self.offset_alloc.dealloc_offset(offset)
    }

    fn flush(&self) {
        self.work.sync_all().unwrap();
    }

    fn reset(&self) {
        self.base_len.store(0, Ordering::Release);
        assert!(self.work.set_len(0).is_ok());
        self.offset_alloc.reset(0);
    }

    fn open(path: impl AsRef<Path>) -> Self
    where
        Self: Sized,
    {
        let work = OpenOptions::new().read(true).write(true).create(true).truncate(false).open(path.as_ref()).unwrap();
        let len = work.metadata().unwrap().len() as usize;
        Self {
            base: None,
            base_len: AtomicUsize::new(0),
            work,
            written: Vec::new(),
            copy_up_lock: Mutex::new(()),
            offset_alloc: OffsetAlloc::new_with(len),
        }
    }
}

/// Reads `buf` at `offset`, zero-filling past the end of the file like the bytes [`super::StdVfs`] never wrote.
#[cfg(unix)]
fn read_exact_at(file: &File, mut buf: &mut [u8], mut offset: usize) {
    while !buf.is_empty() {
        let n = file.read_at(buf, offset as u64).unwrap();
        if n == 0 {
            buf.fill(0);
            return;
        }
        buf = &mut buf[n..];
        offset += n;
    }
}

#[cfg(windows)]
fn read_exact_at(file: &File, mut buf: &mut [u8], mut offset: usize) {
    while !buf.is_empty() {
        let n = file.seek_read(buf, offset as u64).unwrap();
        if n == 0 {
            buf.fill(0);
            return;
        }
        buf = &mut buf[n..];
        offset += n;
    }
}

#[cfg(unix)]
fn write_all_at(file: &File, buf: &[u8], offset: usize) {
    file.write_all_at(buf, offset as u64).unwrap();
}

#[cfg(windows)]
fn write_all_at(file: &File, mut buf: &[u8], mut offset: usize) {
    while !buf.is_empty() {
        let n = file.seek_write(buf, offset as u64).unwrap();
        buf = &buf[n..];
        offset += n;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(name: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("bftree-overlay-{name}-{}", std::process::id()));
        _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn reads_base_until_written_and_never_modifies_base() {
        let dir = temp_dir("rw");
        let base_path = dir.join("base");
        let base_bytes: Vec<u8> = (0..4 * DISK_PAGE_SIZE).map(|i| (i / DISK_PAGE_SIZE) as u8 + 1).collect();
        std::fs::write(&base_path, &base_bytes).unwrap();

        let vfs = OverlayVfs::open_overlay(&base_path, &dir.join("work")).unwrap();
        assert!(!dir.join("work").exists() || cfg!(windows));

        let mut buf = vec![0u8; 4 * DISK_PAGE_SIZE];
        vfs.read(0, &mut buf);
        assert_eq!(buf, base_bytes);

        // Full-page overwrite of page 1, partial write into page 3, and an append past the base.
        vfs.write(DISK_PAGE_SIZE, &vec![0xAA; DISK_PAGE_SIZE]);
        vfs.write(3 * DISK_PAGE_SIZE + 100, &[0xBB; 10]);
        let appended = vfs.alloc_offset(DISK_PAGE_SIZE);
        assert_eq!(appended, 4 * DISK_PAGE_SIZE);
        vfs.write(appended, &vec![0xCC; DISK_PAGE_SIZE]);

        let mut all = vec![0u8; 5 * DISK_PAGE_SIZE];
        vfs.read(0, &mut all);
        let mut expected = base_bytes.clone();
        expected[DISK_PAGE_SIZE..2 * DISK_PAGE_SIZE].fill(0xAA);
        expected[3 * DISK_PAGE_SIZE + 100..3 * DISK_PAGE_SIZE + 110].fill(0xBB);
        expected.extend(std::iter::repeat_n(0xCC, DISK_PAGE_SIZE));
        assert_eq!(all, expected);

        assert_eq!(std::fs::read(&base_path).unwrap(), base_bytes);
        drop(vfs);
        _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn work_path_must_not_exist() {
        let dir = temp_dir("exists");
        std::fs::write(dir.join("base"), vec![1u8; DISK_PAGE_SIZE]).unwrap();
        std::fs::write(dir.join("work"), b"live").unwrap();
        assert!(OverlayVfs::open_overlay(&dir.join("base"), &dir.join("work")).is_err());
        assert_eq!(std::fs::read(dir.join("work")).unwrap(), b"live");
        _ = std::fs::remove_dir_all(&dir);
    }
}
