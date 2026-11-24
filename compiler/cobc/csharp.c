/*
   csharp.c -- C# output mode for cobc's code generator (spike 1)

   Part of the COBOL-for-.NET project, a modified GnuCOBOL 3.2.
   Licensed under the GNU General Public License v3 or later, like the
   rest of cobc.

   This file is #included by codegen.c.  When -fcsharp is given, every
   byte codegen.c writes goes through cs_put() instead of stdio.  Output
   is buffered per FILE into whole lines, and each line is rewritten:

     * class-scope declarations (data storage, fields, attributes,
       constants) become C# static members of types from
       CobolDotNet.Runtime (CobPtr, CobField, CobFieldAttr, ...);
     * statement lines get a token-level C -> C# rewrite:
       "->" -> ".", unary "&" dropped (or "ref" for by-reference
       arguments), C casts mapped or dropped, "3LL" -> "3L",
       NULL -> null, octal string escapes -> \u00XX;
     * preprocessor lines are dropped, except "#define NAME literal",
       which becomes a C# const.

   Lines starting with '\001' are already C# and pass through untouched
   (used by the explicit C# branches in codegen.c).

   Anything the filter can't map is emitted as a "#error CS-TODO" line,
   so the C# compiler stops with a pointer to the unsupported construct.

   This is deliberately a spike technique.  See DECISIONS.md: the
   long-term plan is a structural C# emitter (paragraphs as methods).
*/

#define CS_MAX_FILES	64

struct cs_sbuf {
	char	*p;
	size_t	len;
	size_t	cap;
};

struct cs_file_state {
	FILE		*fp;
	struct cs_sbuf	line;
	int		depth;		/* {} depth outside strings/comments */
	int		in_comment;	/* inside a multi-line comment */
	int		in_pic;		/* inside a cob_pic_symbol initializer */
	int		force_stmt;	/* every line is a statement (method bodies) */
};

static struct cs_file_state	cs_files[CS_MAX_FILES];
static int			cs_nfiles = 0;

/* ---------- small string buffer ---------- */

static void
cs_sb_reserve (struct cs_sbuf *b, size_t extra)
{
	if (b->len + extra + 1 > b->cap) {
		size_t ncap = b->cap ? b->cap * 2 : 256;
		while (ncap < b->len + extra + 1) {
			ncap *= 2;
		}
		b->p = cobc_realloc (b->p, ncap);
		b->cap = ncap;
	}
}

static void
cs_sb_addn (struct cs_sbuf *b, const char *s, size_t n)
{
	cs_sb_reserve (b, n);
	memcpy (b->p + b->len, s, n);
	b->len += n;
	b->p[b->len] = 0;
}

static void
cs_sb_add (struct cs_sbuf *b, const char *s)
{
	cs_sb_addn (b, s, strlen (s));
}

static void
cs_sb_addc (struct cs_sbuf *b, char c)
{
	cs_sb_addn (b, &c, 1);
}

static void
cs_sb_clear (struct cs_sbuf *b)
{
	b->len = 0;
	if (b->p) {
		b->p[0] = 0;
	}
}

/* ---------- helpers ---------- */

static int
cs_is_ident_start (int c)
{
	return isalpha (c) || c == '_';
}

static int
cs_is_ident (int c)
{
	return isalnum (c) || c == '_';
}

static const char *
cs_skip_ws (const char *s)
{
	while (*s == ' ' || *s == '\t') {
		s++;
	}
	return s;
}

static void
cs_rtrim (char *s)
{
	size_t n = strlen (s);
	while (n > 0 && (s[n - 1] == ' ' || s[n - 1] == '\t'
	              || s[n - 1] == '\r')) {
		s[--n] = 0;
	}
}

/* last significant (non-blank) character already written to "o" */
static char
cs_last_sig (const struct cs_sbuf *o)
{
	size_t i = o->len;
	while (i > 0) {
		char c = o->p[i - 1];
		if (c != ' ' && c != '\t') {
			return c;
		}
		i--;
	}
	return 0;
}

/* identifiers passed by address in generated C that need "ref" in C# */
static int
cs_is_byref_ident (const char *id)
{
	if (strcmp (id, "module") == 0
	 || strcmp (id, "cob_glob_ptr") == 0) {
		return 1;
	}
	/* decimal temporaries d_N */
	if (id[0] == 'd' && id[1] == '_' && isdigit ((unsigned char)id[2])) {
		return 1;
	}
	/* file handles h_NAME and key tables k_NAME: cob_file_malloc (&h_X, &k_X, ...) */
	if ((id[0] == 'h' || id[0] == 'k') && id[1] == '_'
	 && strstr (id, "_status") == NULL) {
		return 1;
	}
	return 0;
}

/* C casts (exact text) and their C# replacement; "" drops the cast */
static const char *const cs_casts[][2] = {
	{"(cob_field *)",		""},
	{"(const cob_field *)",		""},
	{"(cob_u8_ptr)",		""},
	{"(cob_u8_t *)",		""},
	{"(unsigned char *)",		""},
	{"(const unsigned char *)",	""},
	{"(void *)",			""},
	{"(char *)",			""},
	{"(const char *)",		""},
	{"(unsigned char)",		"(byte)"},
	{"(cob_u8_t)",			"(byte)"},
	{"(cob_s8_t)",			"(sbyte)"},
	{"(cob_s16_t)",			"(short)"},
	{"(cob_u16_t)",			"(ushort)"},
	{"(cob_s32_t)",			"(int)"},
	{"(cob_u32_t)",			"(uint)"},
	{"(cob_s64_t)",			"(long)"},
	{"(cob_u64_t)",			"(ulong)"},
	{"(size_t)",			"(long)"},
	{"(unsigned int)",		"(uint)"},
	{"(unsigned short)",		"(ushort)"},
	{"(unsigned long long)",	"(ulong)"},
	{"(long long)",			"(long)"},
	{"(signed char)",		"(sbyte)"},
	{NULL, NULL}
};

/* Dereference through a pointer cast:  *(T *)(p)  ->  (p).ACC  where ACC is a
   typed view on CobPtr (S16, U32, ...), or (p)[0] for byte pointers.
   Types are compared with all blanks removed.  Codegen writes these for binary
   (COMP) fields accessed in place, e.g. a COMP subscript, and for single-byte
   stores such as initializing a PIC 9 field. */
static const char *const cs_deref_types[][2] = {
	{"cob_u8_ptr",			".U8"},
	{"cob_s8_ptr",			".S8"},
	{"cob_u16_ptr",			".U16"},
	{"cob_s16_ptr",			".S16"},
	{"cob_u32_ptr",			".U32"},
	{"cob_s32_ptr",			".S32"},
	{"cob_u64_ptr",			".U64"},
	{"cob_s64_ptr",			".S64"},
	{"cob_s8_t*",			".S8"},
	{"signedchar*",			".S8"},
	{"unsignedchar*",		".U8"},
	{"cob_u8_t*",			".U8"},
	{"char*",			".U8"},
	{"short*",			".S16"},
	{"cob_s16_t*",			".S16"},
	{"unsignedshort*",		".U16"},
	{"cob_u16_t*",			".U16"},
	{"int*",			".S32"},
	{"cob_s32_t*",			".S32"},
	{"unsignedint*",		".U32"},
	{"cob_u32_t*",			".U32"},
	{"cob_s64_t*",			".S64"},
	{"longlong*",			".S64"},
	{"cob_u64_t*",			".U64"},
	{"unsignedlonglong*",		".U64"},
	{"float*",			".F32"},
	{"double*",			".F64"},
	{NULL, NULL}
};

/* accessor for a cast's type text (between the parentheses), or NULL */
static const char *
cs_deref_accessor (const char *t, size_t len)
{
	char	buf[64];
	size_t	i, n = 0;

	for (i = 0; i < len && n + 1 < sizeof (buf); i++) {
		if (t[i] != ' ' && t[i] != '\t') {
			buf[n++] = t[i];
		}
	}
	buf[n] = 0;
	if (strncmp (buf, "const", 5) == 0) {
		memmove (buf, buf + 5, n - 4);
	}
	for (i = 0; cs_deref_types[i][0]; i++) {
		if (strcmp (buf, cs_deref_types[i][0]) == 0) {
			return cs_deref_types[i][1];
		}
	}
	return NULL;
}

/* A file's key table k_NAME is an array in C#:
     k_IDX->field         ->  k_IDX[0]->field
     (k_IDX + 2)->field   ->  k_IDX[2]->field
   Returns a new string, or NULL when the line has no such access. */
static char *
cs_keyptr_rewrite (const char *s)
{
	struct cs_sbuf	o = {0};
	int		changed = 0;

	while (*s) {
		/* (k_NAME + N)-> */
		if (s[0] == '(' && s[1] == 'k' && s[2] == '_') {
			const char *p = s + 1, *nb, *ne, *db, *de;
			nb = p;
			while (cs_is_ident (*p)) {
				p++;
			}
			ne = p;
			p = cs_skip_ws (p);
			if (*p == '+') {
				p = cs_skip_ws (p + 1);
				db = p;
				while (isdigit ((unsigned char)*p)) {
					p++;
				}
				de = p;
				p = cs_skip_ws (p);
				if (de > db && p[0] == ')' && p[1] == '-' && p[2] == '>') {
					cs_sb_addn (&o, nb, (size_t)(ne - nb));
					cs_sb_addc (&o, '[');
					cs_sb_addn (&o, db, (size_t)(de - db));
					cs_sb_add (&o, "]->");
					s = p + 3;
					changed = 1;
					continue;
				}
			}
		}
		/* k_NAME-> (not part of a longer identifier) */
		if (s[0] == 'k' && s[1] == '_'
		 && (o.len == 0 || !cs_is_ident (o.p[o.len - 1]))) {
			const char *p = s;
			while (cs_is_ident (*p)) {
				p++;
			}
			if (p[0] == '-' && p[1] == '>') {
				cs_sb_addn (&o, s, (size_t)(p - s));
				cs_sb_add (&o, "[0]->");
				s = p + 2;
				changed = 1;
				continue;
			}
			cs_sb_addn (&o, s, (size_t)(p - s));
			s = p;
			continue;
		}
		cs_sb_addc (&o, *s++);
	}
	if (!changed) {
		if (o.p) {
			cobc_free (o.p);
		}
		return NULL;
	}
	return o.p;
}

/* C narrows on assignment, C# doesn't:  (p).S16 = (p).S16 + 1;  is an int on the right.
   For a translated statement whose target is a 1- or 2-byte typed view, wrap the
   right-hand side in the matching cast:  (p).S16 = (short)((p).S16 + 1);
   (Arithmetic is unchecked, so this truncates exactly like the C original.) */
static void
cs_narrow_assign (struct cs_sbuf *o)
{
	static const char *const narrow[][2] = {
		{".S16", "(short)"}, {".U16", "(ushort)"},
		{".S8", "(sbyte)"}, {".U8", "(byte)"},
		{NULL, NULL}
	};
	const char	*p, *eq = NULL, *semi;
	const char	*cast = NULL;
	char		*lhs_end;
	int		i;
	struct cs_sbuf	n = {0};

	if (!o->p) {
		return;
	}
	/* the first plain '=' (not ==, !=, <=, >=, or a compound assignment) */
	for (p = o->p; *p; p++) {
		if (*p == '"' || *p == '\'') {
			const char q = *p++;
			while (*p && *p != q) {
				if (*p == '\\' && p[1]) {
					p++;
				}
				p++;
			}
			if (!*p) {
				return;
			}
			continue;
		}
		if (*p == '=') {
			if (p[1] == '=') {
				return;
			}
			if (p > o->p && strchr ("=!<>+-*/%&|^", p[-1])) {
				return;
			}
			eq = p;
			break;
		}
	}
	if (!eq) {
		return;
	}
	/* the target: strip blanks and closing parentheses, then look at the accessor */
	lhs_end = (char *)eq;
	while (lhs_end > o->p && (lhs_end[-1] == ' ' || lhs_end[-1] == ')')) {
		lhs_end--;
	}
	for (i = 0; narrow[i][0]; i++) {
		const size_t al = strlen (narrow[i][0]);
		if ((size_t)(lhs_end - o->p) >= al
		 && strncmp (lhs_end - al, narrow[i][0], al) == 0) {
			cast = narrow[i][1];
			break;
		}
	}
	if (!cast) {
		return;
	}
	semi = strrchr (eq, ';');
	if (!semi) {
		return;
	}
	cs_sb_addn (&n, o->p, (size_t)(eq + 1 - o->p));
	cs_sb_addc (&n, ' ');
	cs_sb_add (&n, cast);
	cs_sb_addc (&n, '(');
	cs_sb_addn (&n, eq + 1, (size_t)(semi - eq - 1));
	cs_sb_addc (&n, ')');
	cs_sb_add (&n, semi);
	cobc_free (o->p);
	*o = n;
}

/* end of a balanced (...) group starting at s ('(' at s[0]); points at the ')' or NULL */
static const char *
cs_paren_end (const char *s)
{
	int lvl = 0;
	for (; *s; s++) {
		if (*s == '(') {
			lvl++;
		} else if (*s == ')' && --lvl == 0) {
			return s;
		}
	}
	return NULL;
}

/* ---------- token-level C -> C# rewrite of one statement line ---------- */

static void
cs_expr (const char *s, struct cs_sbuf *o)
{
	while (*s) {
		/* comment: copy through the end */
		if (s[0] == '/' && s[1] == '*') {
			const char *e = strstr (s + 2, "*/");
			if (!e) {
				cs_sb_add (o, s);
				return;
			}
			cs_sb_addn (o, s, (size_t)(e + 2 - s));
			s = e + 2;
			continue;
		}
		/* string or char literal: convert escapes */
		if (*s == '"' || *s == '\'') {
			const char q = *s;
			cs_sb_addc (o, *s++);
			while (*s && *s != q) {
				if (*s == '\\') {
					if (s[1] >= '0' && s[1] <= '7') {
						int v = 0, n = 0;
						char tmp[8];
						s++;
						while (n < 3 && *s >= '0' && *s <= '7') {
							v = v * 8 + (*s - '0');
							s++;
							n++;
						}
						sprintf (tmp, "\\u%04X", v);
						cs_sb_add (o, tmp);
						continue;
					}
					if (s[1] == '?') {
						cs_sb_addc (o, '?');
						s += 2;
						continue;
					}
					cs_sb_addn (o, s, 2);
					s += 2;
					continue;
				}
				cs_sb_addc (o, *s++);
			}
			if (*s) {
				cs_sb_addc (o, *s++);
			}
			continue;
		}
		/* pointer member access */
		if (s[0] == '-' && s[1] == '>') {
			cs_sb_addc (o, '.');
			s += 2;
			continue;
		}
		/* logical and */
		if (s[0] == '&' && s[1] == '&') {
			cs_sb_add (o, "&&");
			s += 2;
			continue;
		}
		/* address-of vs. bitwise and */
		if (*s == '&') {
			const char prev = cs_last_sig (o);
			if (cs_is_ident (prev) || prev == ')' || prev == ']') {
				cs_sb_addc (o, '&');	/* binary */
				s++;
				continue;
			}
			s++;
			s = cs_skip_ws (s);
			if (cs_is_ident_start (*s)) {
				const char *b = s;
				char id[256];
				size_t n;
				while (cs_is_ident (*s)) {
					s++;
				}
				n = (size_t)(s - b);
				if (n >= sizeof (id)) {
					n = sizeof (id) - 1;
				}
				memcpy (id, b, n);
				id[n] = 0;
				if (cs_is_byref_ident (id)) {
					cs_sb_add (o, "ref ");
				}
				cs_sb_add (o, id);
			}
			continue;
		}
		/* unary '*' (pointer dereference): *(p)  ->  (p)[0],  *p  ->  p[0] */
		if (*s == '*') {
			const char prev = cs_last_sig (o);
			if (!(cs_is_ident (prev) || prev == ')' || prev == ']')) {
				const char *n = cs_skip_ws (s + 1);
				/* *(T *)OPERAND : typed access (see cs_deref_types) */
				if (*n == '(') {
					const char *ce = cs_paren_end (n);
					const char *acc = ce ? cs_deref_accessor (n + 1, (size_t)(ce - n - 1)) : NULL;
					if (acc) {
						const char *b = cs_skip_ws (ce + 1);
						const char *e = NULL;
						if (*b == '(') {
							e = cs_paren_end (b);
							if (e) {
								e++;
							}
						} else if (cs_is_ident_start (*b)) {
							e = b;
							while (cs_is_ident (*e)) {
								e++;
							}
						}
						if (e) {
							char *opnd = cobc_malloc ((size_t)(e - b) + 1);
							memcpy (opnd, b, (size_t)(e - b));
							opnd[e - b] = 0;
							cs_sb_addc (o, '(');
							cs_expr (opnd, o);
							cs_sb_addc (o, ')');
							cs_sb_add (o, acc);
							cobc_free (opnd);
							s = e;
							continue;
						}
					}
				}
				if (*n == '(') {
					int lvl = 0;
					const char *e = n;
					for (; *e; e++) {
						if (*e == '(') {
							lvl++;
						} else if (*e == ')' && --lvl == 0) {
							break;
						}
					}
					if (*e == ')') {
						char *inner = cobc_malloc ((size_t)(e - n));
						memcpy (inner, n + 1, (size_t)(e - n - 1));
						inner[e - n - 1] = 0;
						cs_sb_addc (o, '(');
						cs_expr (inner, o);
						cs_sb_add (o, ")[0]");
						cobc_free (inner);
						s = e + 1;
						continue;
					}
				} else if (cs_is_ident_start (*n)) {
					const char *b = n;
					while (cs_is_ident (*n)) {
						n++;
					}
					cs_sb_addn (o, b, (size_t)(n - b));
					cs_sb_add (o, "[0]");
					s = n;
					continue;
				}
			}
			cs_sb_addc (o, *s++);
			continue;
		}
		/* casts */
		if (*s == '(') {
			int i, hit = 0;
			/* function pointer cast: (void *(*)())NAME -> cob_funcptr(NAME) */
			if (strncmp (s, "(void *(*)())", 13) == 0) {
				const char *b;
				s += 13;
				b = s;
				while (cs_is_ident (*s)) {
					s++;
				}
				cs_sb_add (o, "cob_funcptr(");
				cs_sb_addn (o, b, (size_t)(s - b));
				cs_sb_addc (o, ')');
				continue;
			}
			for (i = 0; cs_casts[i][0]; i++) {
				const size_t n = strlen (cs_casts[i][0]);
				if (strncmp (s, cs_casts[i][0], n) == 0) {
					cs_sb_add (o, cs_casts[i][1]);
					s += n;
					hit = 1;
					break;
				}
			}
			if (hit) {
				continue;
			}
			cs_sb_addc (o, *s++);
			continue;
		}
		/* identifiers */
		if (cs_is_ident_start (*s)) {
			const char *b = s;
			size_t n;
			while (cs_is_ident (*s)) {
				s++;
			}
			n = (size_t)(s - b);
			if (n == 4 && strncmp (b, "NULL", 4) == 0) {
				cs_sb_add (o, "null");
			} else {
				cs_sb_addn (o, b, n);
			}
			continue;
		}
		/* numbers: drop/convert C integer suffixes */
		if (isdigit ((unsigned char)*s)) {
			const char *b = s;
			int has_u = 0, has_l = 0;
			if (s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) {
				s += 2;
				while (isxdigit ((unsigned char)*s)) {
					s++;
				}
			} else {
				while (isdigit ((unsigned char)*s) || *s == '.') {
					s++;
				}
			}
			cs_sb_addn (o, b, (size_t)(s - b));
			while (*s == 'u' || *s == 'U' || *s == 'l' || *s == 'L') {
				if (*s == 'u' || *s == 'U') {
					has_u = 1;
				} else {
					has_l = 1;
				}
				s++;
			}
			if (has_u && has_l) {
				cs_sb_add (o, "UL");
			} else if (has_u) {
				cs_sb_add (o, "U");
			} else if (has_l) {
				cs_sb_add (o, "L");
			}
			continue;
		}
		cs_sb_addc (o, *s++);
	}
}

/* ---------- declarations at class scope ---------- */

/* map a scalar C type (already without const/static) to C#; NULL if unknown */
static const char *
cs_scalar_type (const char *t)
{
	static const char *const map[][2] = {
		{"int",			"int"},
		{"signed int",		"int"},
		{"unsigned int",	"uint"},
		{"unsigned",		"uint"},
		{"short",		"short"},
		{"unsigned short",	"ushort"},
		{"long",		"long"},
		{"unsigned long",	"ulong"},
		{"long long",		"long"},
		{"unsigned long long",	"ulong"},
		{"size_t",		"long"},
		{"cob_s8_t",		"sbyte"},
		{"cob_s16_t",		"short"},
		{"cob_u16_t",		"ushort"},
		{"cob_s32_t",		"int"},
		{"cob_u32_t",		"uint"},
		{"cob_s64_t",		"long"},
		{"cob_u64_t",		"ulong"},
		{"double",		"double"},
		{"float",		"float"},
		{"char",		"char"},
		{NULL, NULL}
	};
	int i;
	for (i = 0; map[i][0]; i++) {
		if (strcmp (t, map[i][0]) == 0) {
			return map[i][1];
		}
	}
	return NULL;
}

/* strip a "{ ... }" initializer to its inner text */
static int
cs_brace_inner (const char *rhs, struct cs_sbuf *inner)
{
	const char *b = cs_skip_ws (rhs);
	const char *e;
	if (*b != '{') {
		return 0;
	}
	e = strrchr (b, '}');
	if (!e) {
		return 0;
	}
	cs_sb_clear (inner);
	{
		/* convert the inner expression text */
		char *tmp = cobc_malloc ((size_t)(e - b));
		memcpy (tmp, b + 1, (size_t)(e - b - 1));
		tmp[e - b - 1] = 0;
		cs_expr (tmp, inner);
		cobc_free (tmp);
	}
	return 1;
}

/*
  Convert one class-scope C declaration line to C#.
  Returns 1 on success (written to "o"), 0 if not recognized.
*/
static int
cs_decl (struct cs_file_state *st, const char *line, struct cs_sbuf *o)
{
	char		buf[4096];
	char		*comment = NULL;
	char		*lhs, *rhs = NULL, *p;
	char		*words[32];
	int		nwords = 0, nptr = 0, is_const = 0;
	char		name[256], dim[64];
	char		type[256];
	int		has_dim = 0, ends_open = 0;
	const char	*cs_type = NULL;
	struct cs_sbuf	init = {0}, inner = {0};
	int		i;

	if (strlen (line) >= sizeof (buf)) {
		return 0;
	}
	strcpy (buf, line);

	/* split off trailing comment (outside quotes) */
	{
		int inq = 0;
		for (p = buf; *p; p++) {
			if (*p == '"' && (p == buf || p[-1] != '\\')) {
				inq = !inq;
			} else if (!inq && p[0] == '/' && p[1] == '*') {
				comment = cobc_strdup (p);
				*p = 0;
				break;
			}
		}
	}
	/* drop attributes */
	while ((p = strstr (buf, "__attribute__")) != NULL) {
		char *q = strstr (p, "))");
		if (!q) {
			break;
		}
		memmove (p, q + 2, strlen (q + 2) + 1);
	}
	cs_rtrim (buf);
	{
		size_t n = strlen (buf);
		if (n == 0) {
			goto fail;
		}
		if (buf[n - 1] == ';') {
			buf[n - 1] = 0;
		} else if (buf[n - 1] == '{') {
			ends_open = 1;		/* "x[] = {" multi-line initializer */
			buf[n - 1] = 0;
		} else {
			goto fail;
		}
	}
	/* split lhs = rhs (first '=' outside quotes) */
	{
		int inq = 0;
		for (p = buf; *p; p++) {
			if (*p == '"') {
				inq = !inq;
			} else if (!inq && *p == '=') {
				*p = 0;
				rhs = p + 1;
				break;
			}
		}
	}
	lhs = buf;
	cs_rtrim (lhs);
	/* tokenize lhs, counting '*' */
	for (p = lhs; *p; ) {
		while (*p == ' ' || *p == '\t') {
			p++;
		}
		if (!*p) {
			break;
		}
		if (*p == '*') {
			nptr++;
			p++;
			continue;
		}
		words[nwords] = p;
		while (*p && *p != ' ' && *p != '\t' && *p != '*') {
			p++;
		}
		if (*p) {
			char c = *p;
			*p = 0;
			if (c == '*') {
				nptr++;
			}
			p++;
		}
		if (++nwords >= 31) {
			goto fail;
		}
	}
	if (nwords < 2) {
		goto fail;
	}
	/* name (with optional [dim]) is the last word */
	{
		char *w = words[--nwords];
		char *br = strchr (w, '[');
		if (br) {
			char *be = strchr (br, ']');
			if (!be) {
				goto fail;
			}
			*be = 0;
			has_dim = 1;
			strncpy (dim, br + 1, sizeof (dim) - 1);
			dim[sizeof (dim) - 1] = 0;
			*br = 0;
		}
		if (!cs_is_ident_start (*w)) {
			goto fail;
		}
		strncpy (name, w, sizeof (name) - 1);
		name[sizeof (name) - 1] = 0;
	}
	/* type words without storage class / qualifiers */
	type[0] = 0;
	for (i = 0; i < nwords; i++) {
		if (strcmp (words[i], "static") == 0
		 || strcmp (words[i], "extern") == 0
		 || strcmp (words[i], "struct") == 0
		 || strcmp (words[i], "volatile") == 0
		 || strcmp (words[i], "COB_EXT_EXPORT") == 0
		 || strcmp (words[i], "COB_EXT_IMPORT") == 0) {
			continue;
		}
		if (strcmp (words[i], "const") == 0) {
			is_const = 1;
			continue;
		}
		if (type[0]) {
			strcat (type, " ");
		}
		strcat (type, words[i]);
	}

	/* ---- map ---- */
	if (strcmp (type, "cob_u8_t") == 0 || strcmp (type, "unsigned char") == 0
	 || strcmp (type, "cob_u8_ptr") == 0) {
		cs_type = "CobPtr";
		if (has_dim && nptr == 0 && strcmp (type, "cob_u8_ptr") != 0) {
			if (rhs || ends_open) {
				goto fail;	/* initialized byte arrays: later */
			}
			cs_sb_add (&init, "new CobPtr(");
			cs_sb_add (&init, dim);
			cs_sb_add (&init, ")");
		} else if (has_dim) {
			goto fail;
		} else if (rhs) {
			cs_expr (rhs, &init);
		}
	} else if (strcmp (type, "cob_field") == 0) {
		if (nptr > 0 && has_dim) {
			cs_type = "CobField[]";
			cs_sb_add (&init, "new CobField[");
			cs_sb_add (&init, dim);
			cs_sb_add (&init, "]");
		} else if (nptr > 0) {
			cs_type = "CobField";
			if (rhs) {
				cs_expr (rhs, &init);
			}
		} else if (has_dim) {
			goto fail;
		} else {
			cs_type = "CobField";
			if (rhs) {
				if (!cs_brace_inner (rhs, &inner)) {
					goto fail;
				}
				cs_sb_add (&init, "new CobField(");
				cs_sb_add (&init, inner.p ? inner.p : "");
				cs_sb_add (&init, ")");
			} else {
				cs_sb_add (&init, "new CobField()");
			}
		}
	} else if (strcmp (type, "cob_file") == 0 && nptr == 1 && !has_dim) {
		/* FD / SD: the handle is made by cob_file_malloc at initialization */
		cs_type = "CobFile";
	} else if (strcmp (type, "cob_linage") == 0 && nptr == 1 && !has_dim) {
		/* LINAGE settings of a print file (lingptr = h_X->linorkeyptr) */
		cs_type = "CobLinage";
	} else if (strcmp (type, "cob_file_key") == 0 && nptr == 1 && !has_dim) {
		/* RECORD KEY / ALTERNATE KEY table of an indexed file */
		cs_type = "CobFileKey[]";
	} else if (strcmp (type, "cob_field_attr") == 0 && nptr == 0 && !has_dim) {
		cs_type = "CobFieldAttr";
		if (!rhs || !cs_brace_inner (rhs, &inner)) {
			goto fail;
		}
		cs_sb_add (&init, "new CobFieldAttr(");
		cs_sb_add (&init, inner.p ? inner.p : "");
		cs_sb_add (&init, ")");
	} else if (strcmp (type, "cob_pic_symbol") == 0 && has_dim && ends_open) {
		cs_type = "CobPicSymbol[]";
		cs_sb_add (&init, "{");
		st->in_pic = 1;
	} else if (strcmp (type, "cob_screen") == 0 && nptr == 0 && !has_dim && !rhs) {
		/* SCREEN SECTION item; linked up later by COB_SET_SCREEN */
		cs_type = "CobScreen";
		cs_sb_add (&init, "new CobScreen()");
	} else if (strcmp (type, "cob_decimal") == 0 && !has_dim) {
		cs_type = "CobDecimal";
		if (nptr == 0) {
			cs_sb_add (&init, "new CobDecimal()");
		} else if (rhs) {
			cs_expr (rhs, &init);
		}
	} else if (strcmp (type, "cob_module") == 0 && nptr && !has_dim) {
		cs_type = "CobModule";
		if (rhs) {
			cs_expr (rhs, &init);
		}
	} else if (strcmp (type, "cob_global") == 0 && nptr && !has_dim) {
		cs_type = "CobGlobal";
		if (rhs) {
			cs_expr (rhs, &init);
		}
	} else if (strcmp (type, "cob_frame") == 0
	        || strcmp (type, "cob_frame_ext") == 0) {
		cs_type = "CobFramePtr";
		if (has_dim && nptr == 0) {
			cs_sb_add (&init, "new CobFramePtr(");
			cs_sb_add (&init, dim);
			cs_sb_add (&init, ")");
		} else if (has_dim) {
			goto fail;
		}
	} else if (strcmp (type, "char") == 0 && nptr == 1 && !has_dim) {
		cs_type = "string";
		if (rhs) {
			cs_expr (rhs, &init);
		}
	} else if (nptr == 0) {
		const char *sc = cs_scalar_type (type);
		if (!sc) {
			goto fail;
		}
		if (has_dim) {
			static char arr[64];
			sprintf (arr, "%s[]", sc);
			cs_type = arr;
			if (rhs) {
				if (!cs_brace_inner (rhs, &inner)) {
					goto fail;
				}
				cs_sb_add (&init, "new ");
				cs_sb_add (&init, arr);
				cs_sb_add (&init, " {");
				cs_sb_add (&init, inner.p ? inner.p : "");
				cs_sb_add (&init, "}");
			} else {
				cs_sb_add (&init, "new ");
				cs_sb_add (&init, sc);
				cs_sb_add (&init, "[");
				cs_sb_add (&init, dim);
				cs_sb_add (&init, "]");
			}
		} else {
			cs_type = sc;
			if (rhs) {
				cs_expr (rhs, &init);
			}
		}
	} else {
		goto fail;
	}

	/* ---- emit ---- */
	cs_sb_add (o, "static ");
	/* "const T *p" is a const pointee, not a const variable */
	if (is_const && nptr == 0 && !st->in_pic) {
		cs_sb_add (o, "readonly ");
	}
	cs_sb_add (o, cs_type);
	cs_sb_add (o, " ");
	cs_sb_add (o, name);
	if (init.len) {
		/* "null" initializers are the default; skip them */
		const char *iv = cs_skip_ws (init.p);
		if (strcmp (iv, "null") != 0) {
			cs_sb_add (o, " = ");
			cs_sb_add (o, iv);
		}
	}
	if (!st->in_pic) {
		cs_sb_add (o, ";");
	}
	if (comment) {
		cs_sb_add (o, "\t");
		cs_sb_add (o, comment);
		cobc_free (comment);
	}
	if (init.p) {
		cobc_free (init.p);
	}
	if (inner.p) {
		cobc_free (inner.p);
	}
	return 1;

fail:
	if (comment) {
		cobc_free (comment);
	}
	if (init.p) {
		cobc_free (init.p);
	}
	if (inner.p) {
		cobc_free (inner.p);
	}
	return 0;
}

/* portable strtok_r replacement */
static char *
cs_strtok (char *s, const char *delims, char **save)
{
	char *b;
	if (!s) {
		s = *save;
	}
	if (!s) {
		return NULL;
	}
	while (*s && strchr (delims, *s)) {
		s++;
	}
	if (!*s) {
		*save = NULL;
		return NULL;
	}
	b = s;
	while (*s && !strchr (delims, *s)) {
		s++;
	}
	if (*s) {
		*s++ = 0;
		*save = s;
	} else {
		*save = NULL;
	}
	return b;
}

/* function parameter list "const int entry, cob_module *module" -> C# */
static void
cs_params (const char *in, struct cs_sbuf *o)
{
	char		buf[1024];
	char		*tok, *save = NULL;
	int		first = 1;

	strncpy (buf, in, sizeof (buf) - 1);
	buf[sizeof (buf) - 1] = 0;
	for (tok = cs_strtok (buf, ",", &save); tok; tok = cs_strtok (NULL, ",", &save)) {
		char	type[256] = "", *w, *wsave = NULL, *last = NULL;
		int	nptr = 0;
		const char *cs = NULL;
		char	*words[16];
		int	nw = 0, i;

		for (w = tok; *w; w++) {
			if (*w == '*') {
				nptr++;
				*w = ' ';
			}
		}
		for (w = cs_strtok (tok, " \t", &wsave); w && nw < 16; w = cs_strtok (NULL, " \t", &wsave)) {
			if (strcmp (w, "const") == 0) {
				continue;
			}
			words[nw++] = w;
		}
		if (nw == 0 || (nw == 1 && strcmp (words[0], "void") == 0)) {
			continue;
		}
		last = words[nw - 1];
		for (i = 0; i < nw - 1; i++) {
			if (type[0]) {
				strcat (type, " ");
			}
			strcat (type, words[i]);
		}
		if (nptr == 0) {
			cs = cs_scalar_type (type);
		} else if (strcmp (type, "cob_field") == 0) {
			cs = "CobField";
		} else if (strcmp (type, "cob_module") == 0) {
			cs = "CobModule";
		} else if (strcmp (type, "unsigned char") == 0
		        || strcmp (type, "cob_u8_t") == 0) {
			cs = "CobPtr";
		} else if (strcmp (type, "char") == 0) {
			cs = "string";
		}
		if (!first) {
			cs_sb_add (o, ", ");
		}
		first = 0;
		cs_sb_add (o, cs ? cs : "object /* CS-TODO param type */");
		cs_sb_add (o, " ");
		cs_sb_add (o, last);
	}
}

/* ---------- per-line driver ---------- */

static void
cs_scan_depth (struct cs_file_state *st, const char *s)
{
	while (*s) {
		if (st->in_comment) {
			if (s[0] == '*' && s[1] == '/') {
				st->in_comment = 0;
				s += 2;
				continue;
			}
			s++;
			continue;
		}
		if (s[0] == '/' && s[1] == '*') {
			st->in_comment = 1;
			s += 2;
			continue;
		}
		if (s[0] == '/' && s[1] == '/') {
			return;
		}
		if (*s == '"' || *s == '\'') {
			const char q = *s++;
			while (*s && *s != q) {
				if (*s == '\\' && s[1]) {
					s++;
				}
				s++;
			}
			if (*s) {
				s++;
			}
			continue;
		}
		if (*s == '{') {
			st->depth++;
		} else if (*s == '}') {
			st->depth--;
		}
		s++;
	}
}

static void
cs_write_line (struct cs_file_state *st, const char *s)
{
	fputs (s, st->fp);
	fputc ('\n', st->fp);
}

/* Spike 4: debugging in COBOL terms.  codegen writes a comment
     "Line: 36        : ACCEPT             : SCREENS.cbl"
   before each statement; it becomes  #line 36 "D:/full/path/SCREENS.cbl"
   so the PDB maps the C# that follows to the COBOL line (breakpoints,
   stepping, stack traces).  Plumbing is marked "#line hidden". */
static char	cs_line_file[COB_FILE_MAX + 1];	/* last name seen in a Line comment */
static char	cs_line_full[COB_FILE_MAX + 1];	/* ... resolved to a full path */

static void
cs_line_directive (struct cs_file_state *st, const char *t)
{
	int		lineno;
	const char	*f, *e, *p;
	char		name[COB_FILE_MAX + 1];
	size_t		n;

	if (strncmp (t, "/* Line: ", 9) != 0 || sscanf (t + 9, "%d", &lineno) != 1
	 || lineno <= 0) {
		return;
	}
	/* the file name is the last field: after the last ": " */
	f = NULL;
	for (p = t + 9; (p = strstr (p, ": ")) != NULL; p += 2) {
		f = p + 2;
	}
	e = t + strlen (t) - 2;			/* at the closing comment */
	if (!f || f >= e) {
		return;
	}
	f = cs_skip_ws (f);
	while (e > f && e[-1] == ' ') {
		e--;
	}
	n = (size_t)(e - f);
	if (n == 0 || n > COB_FILE_MAX) {
		return;
	}
	memcpy (name, f, n);
	name[n] = 0;
	/* "last source line   :FILE" variant: keep what follows a ':' that
	   isn't a drive letter's */
	for (p = name + n; p > name + 2; p--) {
		if (p[-1] == ':') {
			memmove (name, p, strlen (p) + 1);
			break;
		}
	}
	if (strcmp (name, cs_line_file) != 0) {
		strcpy (cs_line_file, name);
#ifdef _WIN32
		if (!_fullpath (cs_line_full, name, sizeof (cs_line_full)))
#else
		if (!realpath (name, cs_line_full))
#endif
		{
			strcpy (cs_line_full, name);
		}
	}
	fprintf (st->fp, "#line %d \"%s\"\n", lineno, cs_line_full);
}

static void
cs_process_line (struct cs_file_state *st, char *line)
{
	struct cs_sbuf	o = {0};
	const char	*t;
	const int	is_code_file = (st->fp == yyout);
	const int	started_in_comment = st->in_comment;

	cs_rtrim (line);

	t = cs_skip_ws (line);

	/* pre-converted C# (possibly after codegen's indentation): written
	   as-is, not counted in the brace depth */
	if (*t == '\001') {
		cs_write_line (st, t + 1);
		return;
	}

	/* inside a multi-line comment, or blank / comment-only line */
	if (started_in_comment || *t == 0
	 || (t[0] == '/' && t[1] == '*' && strstr (t, "*/") == t + strlen (t) - 2)) {
		if (!started_in_comment) {
			cs_line_directive (st, t);
		}
		cs_write_line (st, line);
		cs_scan_depth (st, line);
		return;
	}

	/* preprocessor */
	if (*t == '#') {
		const char *d = cs_skip_ws (t + 1);
		if (strncmp (d, "define", 6) == 0) {
			char	nm[256], val[1024];
			if (sscanf (d + 6, " %255s %1023[^\n]", nm, val) == 2) {
				const char *v = cs_skip_ws (val);
				if (*v == '"') {
					fprintf (st->fp, "const string %s = %s;\n", nm, v);
				} else if (isdigit ((unsigned char)*v)
				        || (*v == '-' && isdigit ((unsigned char)v[1]))) {
					fprintf (st->fp, "const int %s = %s;\n", nm, v);
				}
			}
		}
		return;	/* other directives: dropped */
	}

	/* pic symbol initializer rows: {'$', 1}, -> new CobPicSymbol('$', 1), */
	if (st->in_pic) {
		if (strncmp (t, "};", 2) == 0) {
			st->in_pic = 0;
			cs_write_line (st, "};");
			st->depth--;
			return;
		} else {
			const char *b = strchr (t, '{');
			const char *e = strrchr (t, '}');
			if (b && e && e > b) {
				char tmp[256];
				size_t n = (size_t)(e - b - 1);
				if (n >= sizeof (tmp)) {
					n = sizeof (tmp) - 1;
				}
				memcpy (tmp, b + 1, n);
				tmp[n] = 0;
				cs_sb_add (&o, "\tnew CobPicSymbol(");
				cs_expr (tmp, &o);
				cs_sb_add (&o, "),");
				cs_write_line (st, o.p);
				cobc_free (o.p);
				return;
			}
		}
	}

	if (st->depth == 0 && !st->force_stmt) {
		/* class scope */
		if (is_code_file) {
			/* function header: lone return type */
			if (strcmp (t, "int") == 0 || strcmp (t, "static int") == 0) {
				cs_write_line (st, "public static int");
				return;
			}
			if (strcmp (t, "void") == 0 || strcmp (t, "static void") == 0) {
				cs_write_line (st, "public static void");
				return;
			}
			/* "NAME (params)" or "static TYPE NAME (params)" without ';' */
			if (t[strlen (t) - 1] == ')' && !strchr (t, ';')) {
				const char *lp = strchr (t, '(');
				const char *rp = strrchr (t, ')');
				char head[512], params[1024];
				size_t hn = (size_t)(lp - t);
				if (hn < sizeof (head) && (size_t)(rp - lp - 1) < sizeof (params)) {
					memcpy (head, t, hn);
					head[hn] = 0;
					cs_rtrim (head);
					memcpy (params, lp + 1, (size_t)(rp - lp - 1));
					params[rp - lp - 1] = 0;
					if (strncmp (head, "static ", 7) == 0) {
						const char *h = cs_skip_ws (head + 7);
						cs_sb_add (&o, "public static ");
						cs_sb_add (&o, h);
					} else {
						cs_sb_add (&o, head);
					}
					cs_sb_add (&o, " (");
					cs_params (params, &o);
					cs_sb_add (&o, ")");
					cs_write_line (st, o.p);
					cobc_free (o.p);
					return;
				}
			}
		}
		/* everything else at class scope must be a declaration */
		if (cs_decl (st, t, &o)) {
			cs_write_line (st, o.p);
			cobc_free (o.p);
			if (st->in_pic) {
				st->depth++;
			}
			return;
		}
		if (strcmp (t, "{") == 0 || strcmp (t, "}") == 0) {
			cs_write_line (st, line);
			cs_scan_depth (st, line);
			return;
		}
		fprintf (st->fp, "#error CS-TODO unsupported declaration: %s\n", t);
		if (o.p) {
			cobc_free (o.p);
		}
		return;
	}

	/* statement */
	{
		char *kl = cs_keyptr_rewrite (line);
		cs_expr (kl ? kl : line, &o);
		if (kl) {
			cobc_free (kl);
		}
	}
	cs_narrow_assign (&o);
	cs_write_line (st, o.p ? o.p : "");
	cs_scan_depth (st, o.p ? o.p : "");
	if (o.p) {
		cobc_free (o.p);
	}
}

static struct cs_file_state *
cs_state (FILE *fp)
{
	int i;
	for (i = 0; i < cs_nfiles; i++) {
		if (cs_files[i].fp == fp) {
			return &cs_files[i];
		}
	}
	if (cs_nfiles >= CS_MAX_FILES) {
		cobc_err_msg ("-fcsharp: too many output files");
		cobc_abort_terminate (0);
	}
	memset (&cs_files[cs_nfiles], 0, sizeof (cs_files[0]));
	cs_files[cs_nfiles].fp = fp;
	return &cs_files[cs_nfiles++];
}

/* entry point used by codegen.c's output routines */
static void
cs_put (FILE *fp, const char *s)
{
	struct cs_file_state *st;
	if (!fp) {
		return;
	}
	st = cs_state (fp);
	for (; *s; s++) {
		if (*s == '\n') {
			cs_process_line (st, st->line.p ? st->line.p : (char *)"");
			cs_sb_clear (&st->line);
		} else {
			cs_sb_addc (&st->line, *s);
		}
	}
}

/* flush partial lines and forget all file states */
static void
cs_flush_all (void)
{
	int i;
	for (i = 0; i < cs_nfiles; i++) {
		if (cs_files[i].line.len) {
			cs_process_line (&cs_files[i], cs_files[i].line.p);
		}
		if (cs_files[i].line.p) {
			cobc_free (cs_files[i].line.p);
		}
	}
	cs_nfiles = 0;
}

/* flush one file's partial line and drop its state (before fclose) */
static void
cs_forget (FILE *fp)
{
	int i;
	for (i = 0; i < cs_nfiles; i++) {
		if (cs_files[i].fp == fp) {
			if (cs_files[i].line.len) {
				cs_process_line (&cs_files[i], cs_files[i].line.p);
			}
			if (cs_files[i].line.p) {
				cobc_free (cs_files[i].line.p);
			}
			cs_files[i] = cs_files[--cs_nfiles];
			return;
		}
	}
}

/* ================================================================
   Spike 2: the PROCEDURE DIVISION as C# methods.

   GnuCOBOL's C returns from PERFORM through a switch that jumps back
   into whatever block issued the PERFORM.  C# forbids goto into a
   block, so in C# mode:

     * every SECTION / paragraph / ENTRY label starts a method
       "static int NAME ()" (named after the COBOL label, so the
       debugger's call stack reads like the program);
     * falling off the end of a paragraph returns the next label's id;
     * PERFORM a THRU b  ->  frame_ptr++; perform_through = b;
                             if (PROG__run (a) != 0) return -3;
                             frame_ptr--;
     * the implicit "end of b" check returns -2 (PERFORM complete);
     * GO TO x  ->  return x;   GOBACK / EXIT PROGRAM  ->  return -3;
     * PROG__run (id) is a dispatcher loop: call the method for id,
       continue with the id it returns, stop on -2 / -3.

   Local jumps inside one paragraph (EVALUATE ends, EXIT PARAGRAPH,
   NEXT SENTENCE) stay ordinary C# gotos.

   The body is generated into a side file while the program function
   is being written, then appended after that function.
   ================================================================ */

struct cs_para {
	int	id;
	char	*method;
};

static FILE		*cs_body_fp = NULL;
static char		*cs_body_name = NULL;
static int		cs_method_open = 0;
static struct cs_para	*cs_paras = NULL;
static int		cs_npara = 0, cs_cappara = 0;
static char		**cs_names = NULL;	/* method names used in this compile unit */
static int		cs_nnames = 0, cs_capnames = 0;

static int
cs_name_used (const char *n)
{
	int i;
	for (i = 0; i < cs_nnames; i++) {
		if (strcmp (cs_names[i], n) == 0) {
			return 1;
		}
	}
	return 0;
}

static void
cs_name_add (const char *n)
{
	if (cs_nnames >= cs_capnames) {
		cs_capnames = cs_capnames ? cs_capnames * 2 : 64;
		cs_names = cobc_realloc (cs_names, cs_capnames * sizeof (char *));
	}
	cs_names[cs_nnames++] = cobc_strdup (n);
}

static int
cs_is_method_label (const struct cb_label *lp)
{
	/* EXIT SECTION's implicit label sits after the section's last
	   paragraph, i.e. in another method: make it a dispatch target.
	   (EXIT PARAGRAPH / EXIT PERFORM labels stay local gotos.) */
	if (lp->flag_dummy_exit && lp->name
	 && strncmp (lp->name, "EXIT SECTION ", 13) == 0) {
		return 1;
	}
	if (lp->flag_default_handler) {
		return 1;	/* the file error handler: PERFORMed after a failed I/O */
	}
	if (lp->flag_dummy_exit || lp->flag_next_sentence
	 || lp->flag_declarative_exit) {
		return 0;
	}
	return lp->flag_real_label || lp->flag_entry || lp->flag_section
	    || lp->flag_dummy_section || lp->flag_dummy_paragraph;
}

/* C# method name from a COBOL label: "100-MAIN-LOOP" -> "_100_MAIN_LOOP" */
static char *
cs_method_name (const struct cb_label *lp)
{
	const char	*src = lp->orig_name ? lp->orig_name : lp->name;
	char		buf[COB_MINI_BUFF + 32];
	size_t		n = 0;
	const char	*s;

	if (lp->flag_entry) {
		strcpy (buf, "ENTRY_");
		n = 6;
	} else if (src && isdigit ((unsigned char)*src)) {
		buf[n++] = '_';
	}
	for (s = src ? src : "LABEL"; *s && n < COB_MINI_BUFF; s++) {
		buf[n++] = (isalnum ((unsigned char)*s) || *s == '_') ? *s : '_';
	}
	buf[n] = 0;
	if (cs_name_used (buf)) {
		sprintf (buf + n, "_%d", lp->id);
	}
	cs_name_add (buf);
	return cobc_strdup (buf);
}

/* start of a program's PROCEDURE DIVISION: divert output to a side file */
static FILE *
cs_body_begin (const struct cb_program *prog)
{
	static int	seq = 0;
	char		buf[COB_MINI_BUFF + 32];
	size_t		n = strlen (output_name) + 32;

	cs_body_name = cobc_malloc (n);
	snprintf (cs_body_name, n, "%s.body%d", output_name, ++seq);
	cs_body_fp = fopen (cs_body_name, "w+b");
	if (!cs_body_fp) {
		cobc_err_msg ("-fcsharp: cannot create %s", cs_body_name);
		cobc_abort_terminate (0);
	}
	cs_state (cs_body_fp)->force_stmt = 1;
	cs_put (yyout, "\001#line hidden\n");	/* rest of the program function: plumbing */
	cs_npara = 0;
	cs_method_open = 0;
	/* names that already exist as members of the class */
	if (!cs_name_used (prog->program_id)) {
		cs_name_add (prog->program_id);
	}
	snprintf (buf, sizeof (buf), "%s_", prog->program_id);
	cs_name_add (buf);
	snprintf (buf, sizeof (buf), "%s_module_init", prog->program_id);
	cs_name_add (buf);
	return cs_body_fp;
}

/* a SECTION / paragraph / ENTRY label inside the body: new method */
static void
cs_method_label (const struct cb_label *lp)
{
	char		buf[COB_MINI_BUFF * 2 + 128];
	char		*name;
	const char	*kind;

	if (!cs_body_fp) {
		return;
	}
	if (cs_method_open) {
		/* fall through into this label */
		snprintf (buf, sizeof (buf), "\001  return %d;\n\001}\n\n", lp->id);
		cs_put (cs_body_fp, buf);
	}
	name = cs_method_name (lp);
	kind = lp->flag_section ? "SECTION" : (lp->flag_entry ? "ENTRY" : "PARAGRAPH");
	snprintf (buf, sizeof (buf), "\001#line hidden\n\001/* %s %s */\n\001static int %s ()\n\001{\n",
		kind, lp->orig_name ? lp->orig_name : lp->name, name);
	cs_put (cs_body_fp, buf);
	if (cs_npara >= cs_cappara) {
		cs_cappara = cs_cappara ? cs_cappara * 2 : 64;
		cs_paras = cobc_realloc (cs_paras, cs_cappara * sizeof (struct cs_para));
	}
	cs_paras[cs_npara].id = lp->id;
	cs_paras[cs_npara].method = name;
	cs_npara++;
	cs_method_open = 1;
}

/* end of the PROCEDURE DIVISION text: close the last method */
static void
cs_body_end (void)
{
	if (cs_body_fp && cs_method_open) {
		/* falling off the end of the PROCEDURE DIVISION ends the program */
		cs_put (cs_body_fp, "\001  return -3;\n\001}\n\n");
		cs_method_open = 0;
	}
}

/* after the program function: append the methods and the dispatcher */
static void
cs_body_flush (FILE *dest, const struct cb_program *prog)
{
	char	buf[COB_MINI_BUFF * 2 + 64];
	size_t	n;
	int	i;

	if (!cs_body_fp) {
		return;
	}
	cs_forget (cs_body_fp);
	cs_forget (dest);	/* flush dest's partial line, if any */
	fflush (cs_body_fp);
	rewind (cs_body_fp);
	cs_put (dest, "\001\n\001/* ---- PROCEDURE DIVISION: one method per paragraph ---- */\n\n");
	cs_forget (dest);
	while ((n = fread (buf, 1, sizeof (buf), cs_body_fp)) > 0) {
		fwrite (buf, 1, n, dest);
	}
	fclose (cs_body_fp);
	remove (cs_body_name);
	cobc_free (cs_body_name);
	cs_body_fp = NULL;
	cs_body_name = NULL;

	snprintf (buf, sizeof (buf),
		"\001#line hidden\n"
		"\001/* Paragraph dispatcher for PERFORM / GO TO / fall-through */\n"
		"\001static int %s__run (int p)\n"
		"\001{\n"
		"\001  for (;;)\n"
		"\001  {\n"
		"\001    int r;\n"
		"\001    switch (p)\n"
		"\001    {\n", prog->program_id);
	cs_put (dest, buf);
	for (i = 0; i < cs_npara; i++) {
		snprintf (buf, sizeof (buf), "\001    case %d: r = %s (); break;\n",
			cs_paras[i].id, cs_paras[i].method);
		cs_put (dest, buf);
		cobc_free (cs_paras[i].method);
	}
	cs_put (dest,
		"\001    default: cob_fatal_error (COB_FERROR_CODEGEN); return -3;\n"
		"\001    }\n"
		"\001    if (r == -2) return 0;   /* end of PERFORM range */\n"
		"\001    if (r < 0) return r;     /* GOBACK / end of program */\n"
		"\001    p = r;\n"
		"\001  }\n"
		"\001}\n\n");
	cs_npara = 0;
}

/* class header, written once at the top of the (merged) C# file */
static void
cs_prologue (FILE *fp, const struct cb_program *prog)
{
	char buf[512];
	sprintf (buf,
		"\001// <auto-generated> C# translation of COBOL program %s </auto-generated>\n"
		"\001#pragma warning disable CS0162, CS0164, CS0169, CS0219, CS0414, CS8321\n"
		"\001using System;\n"
		"\001using CobolDotNet.Runtime;\n"
		"\001using static CobolDotNet.Runtime.Cob;\n"
		"\001#line hidden\n"
		"\001namespace CobolPrograms {\n"
		"\001public static partial class %s_Program {\n",
		prog->orig_program_id, prog->program_id);
	cs_put (fp, buf);
}

static void
cs_epilogue (FILE *fp)
{
	cs_put (fp, "\001} // class\n\001} // namespace\n");
}
